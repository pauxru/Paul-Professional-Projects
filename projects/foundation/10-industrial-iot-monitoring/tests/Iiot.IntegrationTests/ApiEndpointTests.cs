using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Iiot.Domain;

namespace Iiot.IntegrationTests;

public sealed class ApiEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task DeviceAndTelemetry_HappyPath_PersistsAndQueriesReadings()
    {
        using var client = factory.CreateClient();
        var token = await TokenAsync(client, "admin operator");
        var (deviceId, key) = await RegisterAsync(client, token, "api-happy");
        var reading = Reading(deviceId, 1);
        using var ingest = JsonContent.Create(new[] { reading }, options: Json);
        ingest.Headers.Add("X-Device-Id", deviceId);
        ingest.Headers.Add("X-Device-Key", key);

        var ingested = await client.PostAsync("/api/v1/telemetry", ingest);
        var query = await client.GetAsync($"/api/v1/telemetry/{deviceId}?from=2026-09-01T00:00:00Z&to=2026-09-02T00:00:00Z", token);

        Assert.Equal(HttpStatusCode.OK, ingested.StatusCode);
        Assert.Equal(HttpStatusCode.OK, query.StatusCode);
        var readings = await query.Content.ReadFromJsonAsync<TelemetryReading[]>(Json);
        var output = Assert.IsType<TelemetryReading[]>(readings);
        Assert.Single(output);
        Assert.Equal(1, output[0].Sequence);
    }

    [Fact]
    public async Task CreateDevice_InvalidPayload_ReturnsValidationProblemDetails()
    {
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/devices", new
        {
            deviceId = "",
            deviceType = "compressor",
            plant = "plant-a",
            line = "line-1",
            asset = "asset",
            firmwareVersion = "1.0",
            deviceKey = "device-key-12345",
            enrollmentToken = "enrol"
        }, await TokenAsync(client, "admin"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("errors", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Devices_WithoutToken_ReturnsUnauthorized()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/devices");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateDevice_WithOperatorScope_ReturnsForbidden()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/devices", DeviceRequest("api-forbidden"), await TokenAsync(client, "operator"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Telemetry_WithWrongDeviceKey_ReturnsUnauthorized()
    {
        using var client = factory.CreateClient();
        var token = await TokenAsync(client, "admin operator");
        var (deviceId, _) = await RegisterAsync(client, token, "api-key");
        using var content = JsonContent.Create(new[] { Reading(deviceId, 1) }, options: Json);
        content.Headers.Add("X-Device-Id", deviceId);
        content.Headers.Add("X-Device-Key", "wrong-device-key");

        var response = await client.PostAsync("/api/v1/telemetry", content);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Telemetry_RepeatedDeviceSequence_ReturnsDuplicateReceipt()
    {
        using var client = factory.CreateClient();
        var token = await TokenAsync(client, "admin");
        var (deviceId, key) = await RegisterAsync(client, token, "api-duplicate");
        var first = await IngestAsync(client, deviceId, key, Reading(deviceId, 7));
        var second = await IngestAsync(client, deviceId, key, Reading(deviceId, 7));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var document = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.Equal(1, document.RootElement.GetProperty("duplicates").GetInt32());
    }

    [Fact]
    public async Task Firmware_DesiredVersion_UpdatesDeviceTwin()
    {
        using var client = factory.CreateClient();
        var token = await TokenAsync(client, "admin operator");
        var (deviceId, _) = await RegisterAsync(client, token, "api-firmware");

        var firmware = await client.PostAsJsonAsync($"/api/v1/firmware/{deviceId}", new { version = "2.0.0" }, token);
        var twin = await client.GetAsync($"/api/v1/devices/{deviceId}/twin", token);

        Assert.Equal(HttpStatusCode.Accepted, firmware.StatusCode);
        using var document = JsonDocument.Parse(await twin.Content.ReadAsStringAsync());
        Assert.Equal("2.0.0", document.RootElement.GetProperty("desired").GetProperty("firmwareVersion").GetString());
    }

    [Fact]
    public async Task Command_NotPermittedForTank_ReturnsDomainProblem()
    {
        using var client = factory.CreateClient();
        var token = await TokenAsync(client, "admin operator");
        var (deviceId, _) = await RegisterAsync(client, token, "api-tank", "tank");

        var response = await client.PostAsJsonAsync("/api/v1/commands", new
        {
            deviceId,
            commandType = "restart",
            parameters = new { },
            timeoutSeconds = 30
        }, token);

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.Contains("not permitted", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Telemetry_GzipPayload_IsAccepted()
    {
        using var client = factory.CreateClient();
        var token = await TokenAsync(client, "admin");
        var (deviceId, key) = await RegisterAsync(client, token, "api-gzip");
        var bytes = Iiot.EdgeGateway.EdgeCompression.CompressJson(new[] { Reading(deviceId, 44) });
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        content.Headers.Add("X-Device-Id", deviceId);
        content.Headers.Add("X-Device-Key", key);

        var response = await client.PostAsync("/api/v1/telemetry", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<(string DeviceId, string DeviceKey)> RegisterAsync(HttpClient client, string token, string suffix, string type = "compressor")
    {
        var deviceId = $"{suffix}-{Guid.NewGuid():N}";
        var response = await client.PostAsJsonAsync("/api/v1/devices", DeviceRequest(deviceId, type), token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (
            document.RootElement.GetProperty("device").GetProperty("deviceId").GetString()!,
            document.RootElement.GetProperty("deviceKey").GetString()!);
    }

    private static object DeviceRequest(string deviceId, string type = "compressor") => new
    {
        deviceId,
        deviceType = type,
        plant = "plant-a",
        line = "line-1",
        asset = "synthetic-asset",
        firmwareVersion = "1.0.0",
        deviceKey = "device-key-12345",
        enrollmentToken = $"enrol-{deviceId}",
        certificateThumbprint = "SIMULATED-THUMBPRINT"
    };

    private static TelemetryReading Reading(string deviceId, long sequence) =>
        new(deviceId, sequence, new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero),
            new TelemetryValues(58m, 2m, 7.4m, 26m, 185m, 64m, MachineState.Running));

    private static async Task<HttpResponseMessage> IngestAsync(HttpClient client, string deviceId, string key, TelemetryReading reading)
    {
        using var content = JsonContent.Create(new[] { reading }, options: Json);
        content.Headers.Add("X-Device-Id", deviceId);
        content.Headers.Add("X-Device-Key", key);
        return await client.PostAsync("/api/v1/telemetry", content);
    }

    private static async Task<string> TokenAsync(HttpClient client, string scope)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/token", new { subject = "integration-test", scope });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("accessToken").GetString()!;
    }
}

internal static class HttpClientExtensions
{
    public static async Task<HttpResponseMessage> GetAsync(this HttpClient client, string url, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    public static async Task<HttpResponseMessage> PostAsJsonAsync<T>(this HttpClient client, string url, T value, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(value)
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }
}
