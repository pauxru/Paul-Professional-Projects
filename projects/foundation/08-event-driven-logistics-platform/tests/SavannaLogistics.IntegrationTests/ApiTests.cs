using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SavannaLogistics.Application;
using Xunit.Abstractions;

namespace SavannaLogistics.IntegrationTests;

[Collection("api")]
public sealed class ApiTests(ApiFactory factory)
{
    [Fact]
    public async Task Health_ReadyAndLive_ReturnHealthyWithCorrelationAndSecurityHeaders()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Correlation-Id", "integration-correlation");
        using var live = await client.GetAsync("/health/live");
        using var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("integration-correlation", live.Headers.GetValues("X-Correlation-Id").Single());
        Assert.Equal("nosniff", live.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Vehicles_WithoutToken_Returns401()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/vehicles");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Vehicles_ViewerTryingToCreate_Returns403()
    {
        using var client = await factory.CreateAuthorizedClientAsync("viewer");
        using var response = await client.PostAsJsonAsync("/api/v1/vehicles", new
        {
            registration = $"KVIEW-{Guid.NewGuid():N}",
            make = "Isuzu",
            model = "NPR",
            capacityKg = 2000,
            capacityCubicMetres = 10
        });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Vehicles_InvalidRequest_ReturnsProblemDetailsWithErrorsAndTrace()
    {
        using var client = await factory.CreateAuthorizedClientAsync();
        using var response = await client.PostAsJsonAsync("/api/v1/vehicles", new
        {
            registration = "",
            make = "",
            model = "",
            capacityKg = 0,
            capacityCubicMetres = -1
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("errors", out var errors));
        Assert.True(errors.TryGetProperty("registration", out _));
        Assert.True(json.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task FleetRegistry_CreateDriverVehicleAndAssignment_HappyPath()
    {
        using var client = await factory.CreateAuthorizedClientAsync();
        var driverId = await CreateDriverAsync(client);
        var vehicleId = await CreateVehicleAsync(client);

        using var assignment = await client.PostAsJsonAsync(
            $"/api/v1/vehicles/{vehicleId}/assign",
            new { driverId });
        Assert.Equal(HttpStatusCode.OK, assignment.StatusCode);
        var assigned = await assignment.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(driverId, assigned.GetProperty("assignedDriverId").GetGuid());

        using var get = await client.GetAsync($"/api/v1/vehicles/{vehicleId}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
    }

    [Fact]
    public async Task Telemetry_BatchIngest_UpdatesPersistedProjection()
    {
        using var client = await factory.CreateAuthorizedClientAsync();
        var vehicleId = await CreateVehicleAsync(client);
        var now = DateTimeOffset.UtcNow.AddSeconds(-1);
        using var response = await client.PostAsJsonAsync("/api/v1/telemetry", new
        {
            pings = new[]
            {
                Ping(vehicleId, 1, now, 55),
                Ping(vehicleId, 2, now.AddMilliseconds(100), 57)
            }
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, result.GetProperty("accepted").GetInt32());

        await WaitUntilAsync(async () =>
        {
            using var statesResponse = await client.GetAsync("/api/v1/vehicles/states");
            var states = await statesResponse.Content.ReadFromJsonAsync<JsonElement>();
            return states.EnumerateArray().Any(state =>
                state.GetProperty("vehicleId").GetGuid() == vehicleId &&
                state.GetProperty("lastSequenceNumber").GetInt64() == 2);
        });
    }

    [Fact]
    public async Task Telemetry_DuplicateAtCache_IsSuppressed()
    {
        using var client = await factory.CreateAuthorizedClientAsync();
        var vehicleId = await CreateVehicleAsync(client);
        var ping = Ping(vehicleId, 77, DateTimeOffset.UtcNow.AddSeconds(-1), 40);
        using var first = await client.PostAsJsonAsync("/api/v1/telemetry", new { pings = new[] { ping } });
        using var second = await client.PostAsJsonAsync("/api/v1/telemetry", new { pings = new[] { ping } });
        first.EnsureSuccessStatusCode();
        second.EnsureSuccessStatusCode();
        var result = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, result.GetProperty("cacheDuplicates").GetInt32());
        Assert.Equal(0, result.GetProperty("accepted").GetInt32());
    }

    [Fact]
    public async Task Telemetry_EventBehindWatermark_IsPersistedToLateArrivalSink()
    {
        using var client = await factory.CreateAuthorizedClientAsync();
        var vehicleId = await CreateVehicleAsync(client);
        var now = DateTimeOffset.UtcNow.AddSeconds(-2);
        using var first = await client.PostAsJsonAsync("/api/v1/telemetry", new
        {
            pings = new[] { Ping(vehicleId, 10, now, 30) }
        });
        first.EnsureSuccessStatusCode();
        using var late = await client.PostAsJsonAsync("/api/v1/telemetry", new
        {
            pings = new[] { Ping(vehicleId, 9, now.AddSeconds(-1), 30) }
        });
        late.EnsureSuccessStatusCode();
        var lateResult = await late.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, lateResult.GetProperty("lateArrivals").GetInt32());
        using var stats = await client.GetAsync("/api/v1/telemetry/stats");
        var statsJson = await stats.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(statsJson.GetProperty("lateArrivals").GetInt32() >= 1);
    }

    [Fact]
    public async Task Trip_GeofenceEntryAndExit_CompletesSingleStopTrip()
    {
        using var client = await factory.CreateAuthorizedClientAsync();
        var vehicleId = await CreateVehicleAsync(client);
        var routeId = await CreateRouteAsync(client, oneStop: true);
        var tripId = await CreateAndStartTripAsync(client, vehicleId, routeId);
        var now = DateTimeOffset.UtcNow.AddSeconds(-2);
        using var inside = await client.PostAsJsonAsync("/api/v1/telemetry", new
        {
            pings = new[] { Ping(vehicleId, 1, now, 5, -1.286389, 36.817223) }
        });
        inside.EnsureSuccessStatusCode();
        await WaitUntilAsync(() => TripHasStatusAsync(client, tripId, "AtStop"));

        using var outside = await client.PostAsJsonAsync("/api/v1/telemetry", new
        {
            pings = new[] { Ping(vehicleId, 2, now.AddSeconds(1), 30, -1.20, 36.95) }
        });
        outside.EnsureSuccessStatusCode();
        await WaitUntilAsync(() => TripHasStatusAsync(client, tripId, "Completed"));
    }

    [Fact]
    public async Task Replay_SameWindow_DoesNotDuplicateAlerts()
    {
        using var client = await factory.CreateAuthorizedClientAsync();
        var vehicleId = await CreateVehicleAsync(client);
        var from = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var ingest = await client.PostAsJsonAsync("/api/v1/telemetry", new
        {
            pings = new[]
            {
                Ping(vehicleId, 1, from.AddSeconds(1), 110),
                Ping(vehicleId, 2, from.AddSeconds(2), 112)
            }
        });
        ingest.EnsureSuccessStatusCode();
        await WaitUntilAsync(async () => await AlertCountAsync(client, vehicleId) == 1);
        var before = await AlertCountAsync(client, vehicleId);

        using var replay = await client.PostAsJsonAsync("/api/v1/replay", new
        {
            from,
            to = DateTimeOffset.UtcNow.AddMinutes(1),
            speed = 0,
            vehicleId
        });
        replay.EnsureSuccessStatusCode();
        var after = await AlertCountAsync(client, vehicleId);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Replay_RebuildProjection_RecreatesLatestState()
    {
        using var client = await factory.CreateAuthorizedClientAsync();
        var vehicleId = await CreateVehicleAsync(client);
        using var ingest = await client.PostAsJsonAsync("/api/v1/telemetry", new
        {
            pings = new[] { Ping(vehicleId, 501, DateTimeOffset.UtcNow.AddSeconds(-1), 44) }
        });
        ingest.EnsureSuccessStatusCode();
        await WaitUntilAsync(async () => await HasStateAsync(client, vehicleId, 501));

        using var rebuild = await client.PostAsJsonAsync("/api/v1/replay/rebuild-projection", new { });
        rebuild.EnsureSuccessStatusCode();
        Assert.True(await HasStateAsync(client, vehicleId, 501));
    }

    [Fact]
    public async Task Telemetry_InvalidCoordinates_Returns400()
    {
        using var client = await factory.CreateAuthorizedClientAsync();
        var vehicleId = await CreateVehicleAsync(client);
        var invalid = Ping(vehicleId, 1, DateTimeOffset.UtcNow, 40, 99, 36.8);
        using var response = await client.PostAsJsonAsync("/api/v1/telemetry", new { pings = new[] { invalid } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Simulator_InjectsConfiguredFaultsAndDeliversTelemetry()
    {
        using var client = await factory.CreateAuthorizedClientAsync();
        using var response = await client.PostAsJsonAsync("/api/v1/telemetry/simulate", new
        {
            vehicleCount = 2,
            pingsPerVehicle = 30,
            pingsPerSecond = 1000,
            seed = 808,
            duplicateRate = 0.25,
            outOfOrderRate = 0.35,
            dropoutRate = 0.15,
            gpsJitterMetres = 20,
            clockSkewSeconds = 5,
            realTime = false
        });
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(60, result.GetProperty("generated").GetInt32());
        Assert.True(result.GetProperty("dropped").GetInt32() > 0);
        Assert.True(result.GetProperty("duplicatesInjected").GetInt32() > 0);
        Assert.True(result.GetProperty("outOfOrderSwaps").GetInt32() > 0);
    }

    private static object Ping(
        Guid vehicleId,
        long sequence,
        DateTimeOffset timestamp,
        double speed,
        double latitude = -1.286389,
        double longitude = 36.817223) =>
        new
        {
            vehicleId,
            latitude,
            longitude,
            speedKph = speed,
            headingDegrees = 90,
            odometerKm = 1000 + sequence,
            fuelPercent = 70,
            ignition = true,
            deviceTimestamp = timestamp,
            sequenceNumber = sequence
        };

    private static async Task<Guid> CreateVehicleAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/vehicles", new
        {
            registration = $"K-{Guid.NewGuid():N}",
            make = "Isuzu",
            model = "NPR",
            capacityKg = 4500,
            capacityCubicMetres = 24
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateDriverAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/vehicles/drivers", new
        {
            name = "Synthetic Driver",
            licenceNumber = $"DEMO-{Guid.NewGuid():N}",
            phoneAlias = "synthetic"
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateRouteAsync(HttpClient client, bool oneStop)
    {
        var stops = oneStop
            ? new[]
            {
                new { name = "Depot", latitude = -1.286389, longitude = 36.817223, radiusKm = 0.2, dwellMinutes = 0 }
            }
            : new[]
            {
                new { name = "Depot", latitude = -1.286389, longitude = 36.817223, radiusKm = 0.2, dwellMinutes = 0 },
                new { name = "Hub", latitude = -1.21, longitude = 36.923, radiusKm = 0.2, dwellMinutes = 0 }
            };
        using var response = await client.PostAsJsonAsync("/api/v1/routes", new
        {
            name = $"Synthetic route {Guid.NewGuid():N}",
            polyline = new[]
            {
                new { latitude = -1.286389, longitude = 36.817223 },
                new { latitude = -1.21, longitude = 36.923 }
            },
            stops
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("route").GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateAndStartTripAsync(HttpClient client, Guid vehicleId, Guid routeId)
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var create = await client.PostAsJsonAsync("/api/v1/trips", new
        {
            vehicleId,
            routeId,
            plannedStart = start,
            slaDueAt = start.AddHours(2)
        });
        create.EnsureSuccessStatusCode();
        var tripId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var startResponse = await client.PostAsync($"/api/v1/trips/{tripId}/start", null);
        startResponse.EnsureSuccessStatusCode();
        return tripId;
    }

    private static async Task<bool> TripHasStatusAsync(HttpClient client, Guid tripId, string status)
    {
        using var response = await client.GetAsync("/api/v1/trips?pageSize=200");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("items").EnumerateArray().Any(trip =>
            trip.GetProperty("id").GetGuid() == tripId &&
            trip.GetProperty("status").GetString() == status);
    }

    private static async Task<int> AlertCountAsync(HttpClient client, Guid vehicleId)
    {
        using var response = await client.GetAsync("/api/v1/alerts?pageSize=200");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("items").EnumerateArray().Count(alert =>
            alert.GetProperty("vehicleId").GetGuid() == vehicleId);
    }

    private static async Task<bool> HasStateAsync(HttpClient client, Guid vehicleId, long sequence)
    {
        using var response = await client.GetAsync("/api/v1/vehicles/states");
        response.EnsureSuccessStatusCode();
        var states = await response.Content.ReadFromJsonAsync<JsonElement>();
        return states.EnumerateArray().Any(state =>
            state.GetProperty("vehicleId").GetGuid() == vehicleId &&
            state.GetProperty("lastSequenceNumber").GetInt64() == sequence);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate)
    {
        var timeout = Stopwatch.StartNew();
        while (!await predicate())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(10))
            {
                throw new TimeoutException("Condition was not reached within ten seconds.");
            }
            await Task.Delay(30);
        }
    }
}

public sealed class BatchPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Telemetry_TenThousandPingBatch_CompletesWithinBoundedTime()
    {
        await using var factory = new BufferedApiFactory();
        using var client = await factory.CreateAuthorizedClientAsync();
        var vehicleId = Guid.NewGuid();
        using var create = await client.PostAsJsonAsync("/api/v1/vehicles", new
        {
            registration = $"KPERF-{Guid.NewGuid():N}",
            make = "Isuzu",
            model = "NPR",
            capacityKg = 4500,
            capacityCubicMetres = 24
        });
        create.EnsureSuccessStatusCode();
        vehicleId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var timestamp = DateTimeOffset.UtcNow;
        var pings = Enumerable.Range(0, 10_000).Select(index => new
        {
            vehicleId,
            latitude = -1.286389,
            longitude = 36.817223,
            speedKph = 40,
            headingDegrees = 90,
            odometerKm = 10_000 + index,
            fuelPercent = 70,
            ignition = true,
            deviceTimestamp = timestamp,
            sequenceNumber = index
        }).ToArray();
        var watch = Stopwatch.StartNew();
        using var response = await client.PostAsJsonAsync("/api/v1/telemetry", new { pings });
        watch.Stop();
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(10_000, result.GetProperty("accepted").GetInt32());
        output.WriteLine(
            "BATCH_BENCHMARK pings=10000 elapsedMs={0:F2} throughputPerSecond={1:F2}",
            watch.Elapsed.TotalMilliseconds,
            10_000 / watch.Elapsed.TotalSeconds);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"10k ingestion took {watch.Elapsed}.");
    }
}
