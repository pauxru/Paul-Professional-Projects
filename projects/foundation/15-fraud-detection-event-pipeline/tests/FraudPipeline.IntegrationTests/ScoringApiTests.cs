using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FraudPipeline.Api.Contracts;
using FraudPipeline.Api.Endpoints;
using FraudPipeline.Infrastructure.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace FraudPipeline.IntegrationTests;

public class ScoringApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public ScoringApiTests(ApiFactory factory) => _factory = factory;

    private HttpClient AuthenticatedClient(params string[] scopes)
    {
        var client = _factory.CreateClient();
        using var scope = _factory.Services.CreateScope();
        var issuer = scope.ServiceProvider.GetRequiredService<ITokenIssuer>();
        var token = issuer.Issue("integration-test", scopes);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static ScoreRequest ValidScoreRequest(string txnRef, decimal amount = 100m)
        => new()
        {
            TransactionRef = txnRef,
            CardId = "CARD1",
            CustomerId = "CUST1",
            DeviceId = "DEV1",
            IpAddress = "192.0.2.1",
            MerchantId = "MERCH1",
            Mcc = "5411",
            Amount = amount,
            Currency = "USD",
            Type = "CardNotPresent",
            Latitude = 40.71,
            Longitude = -74.00,
            Country = "US"
        };

    [Fact]
    public async Task Score_WithoutJwt_Returns401()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/transactions/score", ValidScoreRequest("TX-INT-01"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Score_WithWrongScope_Returns403()
    {
        var client = AuthenticatedClient("risk:investigate"); // wrong scope for scoring
        var resp = await client.PostAsJsonAsync("/api/v1/transactions/score", ValidScoreRequest("TX-INT-02"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Score_ValidRequest_Returns200WithDecision()
    {
        var client = AuthenticatedClient("risk:score");
        var resp = await client.PostAsJsonAsync("/api/v1/transactions/score", ValidScoreRequest("TX-INT-03"));
        if (resp.StatusCode != HttpStatusCode.OK)
        {
            var body = await resp.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Status: {resp.StatusCode}\nBody: {body}");
        }
        var scored = await resp.Content.ReadFromJsonAsync<ScoreResponse>();
        Assert.NotNull(scored);
        Assert.Equal("TX-INT-03", scored!.TransactionRef);
        Assert.False(string.IsNullOrEmpty(scored.Decision));
        Assert.False(string.IsNullOrEmpty(scored.RulesetVersion));
    }

    [Fact]
    public async Task Score_InvalidMcc_ReturnsProblemDetails()
    {
        var client = AuthenticatedClient("risk:score");
        var req = ValidScoreRequest("TX-INT-04");
        req.Mcc = "abc"; // fails regex
        var resp = await client.PostAsJsonAsync("/api/v1/transactions/score", req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task TokenEndpoint_ReturnsBearerToken()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/token", new TokenRequest
        {
            Subject = "test-user",
            Scopes = new[] { "risk:score" }
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrEmpty(body!.AccessToken));
    }

    [Fact]
    public async Task HealthLive_ReturnsHealthy()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Throughput_ScoresMoreThan5000TransactionsInBoundedTime()
    {
        var client = AuthenticatedClient("risk:score");
        const int total = 5_000;
        var sw = Stopwatch.StartNew();
        int approved = 0, other = 0;
        for (int i = 0; i < total; i++)
        {
            var req = ValidScoreRequest($"TX-THR-{i:D6}");
            var resp = await client.PostAsJsonAsync("/api/v1/transactions/score", req);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadFromJsonAsync<ScoreResponse>();
            if (body!.Decision == "Approve") approved++; else other++;
        }
        sw.Stop();
        // A generous bound but enforces a hard ceiling.
        Assert.True(sw.Elapsed < TimeSpan.FromMinutes(3), $"5000 scores took {sw.Elapsed.TotalSeconds:F1}s");
        Assert.True(approved + other == total);
    }
}
