using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FeatureFlags.Domain;

namespace FeatureFlags.IntegrationTests;

public sealed class ApiSecurityAndSdkTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task ProtectedFlagList_WithoutBearerToken_Returns401()
    {
        await factory.SeedAsync();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/projects/acme/environments/dev/flags");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task FlagWrite_WithReadOnlyToken_Returns403()
    {
        await factory.SeedAsync();
        using var client = await factory.CreateAuthorizedClientAsync("reader", "flags:read");
        var response = await client.PostAsJsonAsync("/api/v1/projects/acme/environments/dev/flags", new { flag = Flag("write-denied") });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateProject_InvalidPayload_ReturnsProblemDetailsValidationShape()
    {
        using var client = await factory.CreateAuthorizedClientAsync();
        var response = await client.PostAsJsonAsync("/api/v1/projects", new { key = "", name = "" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.TryGetProperty("errors", out var errors));
        Assert.True(errors.EnumerateObject().Any());
    }

    [Fact]
    public async Task Responses_IncludeCorrelationAndSecurityHeaders()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Correlation-Id", "test-correlation");
        var response = await client.GetAsync("/health/live");
        Assert.Equal("test-correlation", response.Headers.GetValues("X-Correlation-Id").Single());
        Assert.True(response.Headers.Contains("X-Content-Type-Options"));
        Assert.True(response.Headers.Contains("Content-Security-Policy"));
    }

    [Fact]
    public async Task SdkConfig_ClientKeyExcludesServerOnlyFlags_WhileServerKeyIncludesThem()
    {
        await factory.SeedAsync();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Sdk-Key", "client-dev-acme-public-demo");
        var clientResponse = await client.GetAsync("/api/v1/sdk/config/acme/dev");
        clientResponse.EnsureSuccessStatusCode();
        using var clientConfig = JsonDocument.Parse(await clientResponse.Content.ReadAsStringAsync());
        var clientKeys = clientConfig.RootElement.GetProperty("flags").EnumerateArray().Select(item => item.GetProperty("key").GetString()).ToArray();
        Assert.DoesNotContain("search-config", clientKeys);

        using var serverClient = factory.CreateClient();
        serverClient.DefaultRequestHeaders.Add("X-Sdk-Key", "server-dev-acme-not-a-secret");
        var serverResponse = await serverClient.GetAsync("/api/v1/sdk/config/acme/dev");
        serverResponse.EnsureSuccessStatusCode();
        using var serverConfig = JsonDocument.Parse(await serverResponse.Content.ReadAsStringAsync());
        var serverKeys = serverConfig.RootElement.GetProperty("flags").EnumerateArray().Select(item => item.GetProperty("key").GetString()).ToArray();
        Assert.Contains("search-config", serverKeys);
    }

    [Fact]
    public async Task SdkConfig_IfNoneMatchForCurrentVersion_Returns304()
    {
        await factory.SeedAsync();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Sdk-Key", "client-dev-acme-public-demo");
        var first = await client.GetAsync("/api/v1/sdk/config/acme/dev");
        var etag = first.Headers.ETag?.ToString();
        Assert.False(string.IsNullOrWhiteSpace(etag));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/sdk/config/acme/dev");
        request.Headers.TryAddWithoutValidation("X-Sdk-Key", "client-dev-acme-public-demo");
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var second = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
    }

    [Fact]
    public async Task SdkConfig_InvalidSdkKey_Returns401()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Sdk-Key", "invalid");
        var response = await client.GetAsync("/api/v1/sdk/config/acme/dev");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task EventsEndpoint_AcceptsSdkEventsAndAnalyticsExposesMetrics()
    {
        await factory.SeedAsync();
        using var sdkClient = factory.CreateClient();
        sdkClient.DefaultRequestHeaders.Add("X-Sdk-Key", "client-dev-acme-public-demo");
        var post = await sdkClient.PostAsJsonAsync("/api/v1/events", new
        {
            projectKey = "acme", environmentKey = "dev",
            events = new[] { new { kind = "evaluation", flagKey = "new-checkout", variationIndex = 1, contextKey = "analytics-user", occurredAt = "2026-01-01T00:00:00Z" } }
        });
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        using var admin = await factory.CreateAuthorizedClientAsync();
        var metricResponse = await admin.GetAsync("/api/v1/analytics/acme/dev/metrics");
        metricResponse.EnsureSuccessStatusCode();
        var body = await metricResponse.Content.ReadAsStringAsync();
        Assert.Contains("new-checkout", body);
    }

    private static object Flag(string key) => new
    {
        key, name = key, valueType = "Boolean", isOn = true, clientSide = true, offVariation = 0, fallthroughVariation = 1, salt = "test",
        lifecycleStatus = "Active", variations = new[] { new { index = 0, name = "off", value = false }, new { index = 1, name = "on", value = true } }
    };
}
