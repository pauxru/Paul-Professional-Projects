using System.Net;
using System.Net.Http.Json;
using AuditPlatform.Application.Events;
using AuditPlatform.Domain.Events;
using Xunit;

namespace AuditPlatform.IntegrationTests;

public class HealthTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public HealthTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Health_Live_ReturnsOk()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_Ready_ReturnsOk()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

public class AuthTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AuthTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Ingest_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();
        var body = TestData.SampleIngestBody();
        var response = await client.PostAsJsonAsync("/api/v1/events", body);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Ingest_WithReadOnlyToken_Returns403()
    {
        var client = _factory.CreateAuthClient(scopes: "audit:read");
        var body = TestData.SampleIngestBody();
        var response = await client.PostAsJsonAsync("/api/v1/events", body);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DevTokenEndpoint_MintsToken_AndRoundtripsToRead()
    {
        var client = _factory.CreateClient();
        var tokenResponse = await client.PostAsJsonAsync("/api/v1/auth/dev-token", new
        {
            Subject = "tester",
            TenantId = "tenant-a",
            Scopes = "audit:read audit:verify",
            Clearance = "elevated"
        });
        tokenResponse.EnsureSuccessStatusCode();
        var token = await tokenResponse.Content.ReadFromJsonAsync<TokenResp>();
        Assert.False(string.IsNullOrEmpty(token?.access_token));

        var eventsClient = _factory.CreateClient();
        eventsClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token!.access_token);
        var listResponse = await eventsClient.GetAsync("/api/v1/events");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
    }

    private sealed record TokenResp(string access_token, string token_type, int expires_in);
}
