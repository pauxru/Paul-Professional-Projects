using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Northstar.Reliability.IntegrationTests.Fixtures;

namespace Northstar.Reliability.IntegrationTests;

public sealed class ReliabilityApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task GetServices_WithoutJwt_Returns401()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/services/");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateService_WithReadOnlyJwt_Returns403()
    {
        using var client = await factory.CreateAuthenticatedClientAsync("reliability.read");

        var response = await client.PostAsJsonAsync("/api/v1/services/", ServiceRequest("read-only-denied"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateService_WithInvalidPayload_ReturnsProblemDetails()
    {
        using var client = await factory.CreateAuthenticatedClientAsync("reliability.read", "reliability.write");

        var response = await client.PostAsJsonAsync("/api/v1/services/", new { slug = "", name = "" });
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("https://tools.ietf.org/html/rfc9110#section-15.5.1", payload.GetProperty("type").GetString());
        Assert.True(payload.GetProperty("errors").TryGetProperty("slug", out _));
    }

    [Fact]
    public async Task CreateService_WithWriteScope_Returns201AndCorrelationId()
    {
        using var client = await factory.CreateAuthenticatedClientAsync("reliability.read", "reliability.write");

        var response = await client.PostAsJsonAsync("/api/v1/services/", ServiceRequest("created-service"));
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("created-service", payload.GetProperty("slug").GetString());
        Assert.True(response.Headers.Contains("X-Correlation-Id"));
    }

    [Fact]
    public async Task IngestMetrics_ForUncataloguedService_Returns422()
    {
        using var client = await factory.CreateAuthenticatedClientAsync("reliability.read", "reliability.write");
        var request = new
        {
            samples = new[]
            {
                MetricInput("does-not-exist", 100, 1)
            }
        };

        var response = await client.PostAsJsonAsync("/api/v1/metrics/ingest", request);

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
    }

    [Fact]
    public async Task DeployGate_WithoutSlo_AllowsDeploy()
    {
        using var client = await factory.CreateAuthenticatedClientAsync("reliability.read", "reliability.write");
        var slug = "allow-gate-service";
        await CreateServiceAsync(client, slug);

        var response = await client.GetAsync($"/api/v1/gates/{slug}/deploy");
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(payload.GetProperty("allowed").GetBoolean());
        Assert.Equal("Allow", payload.GetProperty("action").GetString());
    }

    [Fact]
    public async Task DeployGate_WithExhaustedBudget_DeniesDeploy()
    {
        using var client = await factory.CreateAuthenticatedClientAsync("reliability.read", "reliability.write");
        var slug = "deny-gate-service";
        await CreateServiceAsync(client, slug);
        var sliResponse = await client.PostAsJsonAsync("/api/v1/slis/", new
        {
            name = "availability",
            serviceSlug = slug,
            aggregationMode = "RequestBased",
            kind = "Availability",
            endpoint = (string?)null,
            region = (string?)null,
            tier = (string?)null,
            latencyThresholdMilliseconds = (decimal?)null
        });
        sliResponse.EnsureSuccessStatusCode();
        var sliId = await IdFromAsync(sliResponse);
        var sloResponse = await client.PostAsJsonAsync("/api/v1/slos/", new
        {
            name = "99.9 availability",
            sliId,
            serviceSlug = slug,
            target = 0.999m,
            windowKind = "Rolling",
            rollingDays = 30,
            calendarPeriod = "Monthly"
        });
        sloResponse.EnsureSuccessStatusCode();
        var metricResponse = await client.PostAsJsonAsync("/api/v1/metrics/ingest", new
        {
            samples = new[] { MetricInput(slug, 1_000, 10) }
        });
        metricResponse.EnsureSuccessStatusCode();

        var response = await client.GetAsync($"/api/v1/gates/{slug}/deploy");
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(payload.GetProperty("allowed").GetBoolean());
        Assert.Equal("FreezeAllChanges", payload.GetProperty("action").GetString());
    }

    private static object ServiceRequest(string slug) => new
    {
        slug,
        name = $"Service {slug}",
        tier = "Tier1",
        owningTeam = "Reliability",
        onCallRotation = "reliability-primary",
        repositoryUrl = $"https://github.example.invalid/{slug}",
        runbookUrl = $"https://runbooks.example.invalid/{slug}",
        dependencies = Array.Empty<string>()
    };

    private static object MetricInput(string serviceSlug, long requests, long errors) => new
    {
        serviceSlug,
        timestamp = DateTimeOffset.UtcNow,
        endpoint = "/api/checkout",
        region = "eu-west",
        tier = "Tier1",
        requests,
        errors,
        latencyGoodRequests = requests - errors,
        qualityGoodEvents = requests,
        qualityValidEvents = requests,
        freshnessGoodEvents = requests,
        freshnessValidEvents = requests,
        probeGoodMinutes = errors == 0 ? 1 : 0,
        probeTotalMinutes = 1,
        p50LatencyMilliseconds = 70d,
        p95LatencyMilliseconds = 140d,
        latencyHistogram = new[]
        {
            new { upperBoundMilliseconds = 100m, count = requests - errors },
            new { upperBoundMilliseconds = 300m, count = errors }
        },
        resolution = "Minute"
    };

    private static async Task CreateServiceAsync(HttpClient client, string slug)
    {
        var response = await client.PostAsJsonAsync("/api/v1/services/", ServiceRequest(slug));
        response.EnsureSuccessStatusCode();
    }

    private static async Task<Guid> IdFromAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }
}
