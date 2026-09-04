using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Northstar.IntegrationTests.Fixtures;
using Northstar.Infrastructure.Persistence;

namespace Northstar.IntegrationTests;

public sealed class ClaimsApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task GetClaims_WithoutBearerToken_Returns401()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/claims");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.Contains("X-Correlation-Id"));
    }

    [Fact]
    public async Task GetClaims_WithReadScope_ReturnsSeededListing()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(client, "claims:read"));

        var response = await client.GetAsync("/api/v1/claims?page=1&pageSize=10");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.GetProperty("totalCount").GetInt32() >= 1);
        Assert.True(response.Headers.Contains("X-Correlation-Id"));
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task GetClaims_WithInjectedFilter_DoesNotExpandQuery()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(client, "claims:read"));

        var response = await client.GetAsync("/api/v1/claims?policyholder=does-not-exist%27%20OR%201%3D1%20--");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, body.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task PostClaim_WithValidRequest_CreatesClaimWithCalculatedReserve()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(client, "claims:adjust"));
        var reference = $"CLM-API-{Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync("/api/v1/claims", new
        {
            policyNumber = "POL-ACME-001",
            reference,
            claimedAmount = 3400m,
            currency = "USD"
        });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(2_900m, body.GetProperty("reserveAmount").GetDecimal());
        Assert.Equal("Submitted", body.GetProperty("status").GetString());
        Assert.NotNull(response.Headers.Location);
    }

    [Fact]
    public async Task PostClaim_MissingRequiredReference_Returns400ProblemDetails()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(client, "claims:adjust"));

        var response = await client.PostAsJsonAsync("/api/v1/claims", new
        {
            policyNumber = "POL-ACME-001",
            reference = "",
            claimedAmount = 10m,
            currency = "USD"
        });
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(problem);
        Assert.Equal(400, problem.Status);
        Assert.Contains(problem.Errors.Keys, key => string.Equals(key, "Reference", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PostClaim_WithPolicyCurrencyMismatch_Returns422ProblemDetails()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(client, "claims:adjust"));

        var response = await client.PostAsJsonAsync("/api/v1/claims", new
        {
            policyNumber = "POL-ACME-001",
            reference = $"CLM-CURRENCY-{Guid.NewGuid():N}",
            claimedAmount = 10m,
            currency = "KES"
        });
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.NotNull(problem);
        Assert.Equal(422, problem.Status);
        Assert.Contains("currency", problem.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostClaim_WithReadOnlyScope_Returns403()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(client, "claims:read"));

        var response = await client.PostAsJsonAsync("/api/v1/claims", new
        {
            policyNumber = "POL-ACME-001",
            reference = $"CLM-DENIED-{Guid.NewGuid():N}",
            claimedAmount = 10m,
            currency = "USD"
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Assessment_WithStaleVersion_Returns409Conflict()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(client, "claims:adjust"));
        var createResponse = await client.PostAsJsonAsync("/api/v1/claims", new
        {
            policyNumber = "POL-ACME-001",
            reference = $"CLM-CONCURRENT-{Guid.NewGuid():N}",
            claimedAmount = 1_000m,
            currency = "USD"
        });
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();
        var version = created.GetProperty("version").GetInt32();

        var first = await client.PostAsJsonAsync($"/api/v1/claims/{id}/assessment", new
        {
            adjuster = "A. Adjuster",
            reserveAmount = 900m,
            expectedVersion = version
        });
        var stale = await client.PostAsJsonAsync($"/api/v1/claims/{id}/assessment", new
        {
            adjuster = "B. Adjuster",
            reserveAmount = 800m,
            expectedVersion = version
        });
        var problem = await stale.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.NotNull(problem);
        Assert.Equal(409, problem.Status);
    }

    [Fact]
    public async Task ClaimWorkflow_AssessmentApprovalSettlementAndClosure_TransitionsEndToEnd()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(client, "claims:adjust"));
        var create = await client.PostAsJsonAsync("/api/v1/claims", new
        {
            policyNumber = "POL-ACME-001",
            reference = $"CLM-WORKFLOW-{Guid.NewGuid():N}",
            claimedAmount = 3_400m,
            currency = "USD"
        });
        var submitted = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = submitted.GetProperty("id").GetGuid();
        var assessment = await client.PostAsJsonAsync($"/api/v1/claims/{id}/assessment", new
        {
            adjuster = "Workflow Adjuster",
            reserveAmount = 2_900m,
            expectedVersion = submitted.GetProperty("version").GetInt32()
        });
        var underReview = await assessment.Content.ReadFromJsonAsync<JsonElement>();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(client, "claims:approve"));
        var approvedResponse = await client.PostAsJsonAsync($"/api/v1/claims/{id}/transition", new
        {
            targetStatus = "Approved",
            expectedVersion = underReview.GetProperty("version").GetInt32()
        });
        var approved = await approvedResponse.Content.ReadFromJsonAsync<JsonElement>();
        var settledResponse = await client.PostAsJsonAsync($"/api/v1/claims/{id}/settle", new
        {
            expectedVersion = approved.GetProperty("version").GetInt32()
        });
        var settled = await settledResponse.Content.ReadFromJsonAsync<JsonElement>();
        var closedResponse = await client.PostAsJsonAsync($"/api/v1/claims/{id}/transition", new
        {
            targetStatus = "Closed",
            expectedVersion = settled.GetProperty("version").GetInt32()
        });
        var closed = await closedResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, assessment.StatusCode);
        Assert.Equal(HttpStatusCode.OK, approvedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, settledResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, closedResponse.StatusCode);
        Assert.Equal("UnderReview", underReview.GetProperty("status").GetString());
        Assert.Equal("Approved", approved.GetProperty("status").GetString());
        Assert.Equal("Settled", settled.GetProperty("status").GetString());
        Assert.Equal(2_900m, settled.GetProperty("settlementAmount").GetDecimal());
        Assert.Equal("Closed", closed.GetProperty("status").GetString());
    }

    [Fact]
    public async Task PostClaim_Succeeds_AppendsAuditRecordWithCorrelationMetadata()
    {
        int before;
        using (var scope = factory.Services.CreateScope())
        {
            before = await scope.ServiceProvider.GetRequiredService<NorthstarDbContext>().AuditRecords.CountAsync();
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(client, "claims:adjust"));
        client.DefaultRequestHeaders.Add("X-Correlation-Id", "audit-test-correlation");
        var response = await client.PostAsJsonAsync("/api/v1/claims", new
        {
            policyNumber = "POL-ACME-001",
            reference = $"CLM-AUDIT-{Guid.NewGuid():N}",
            claimedAmount = 100m,
            currency = "USD"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var assertionScope = factory.Services.CreateScope();
        var audit = await assertionScope.ServiceProvider.GetRequiredService<NorthstarDbContext>().AuditRecords
            .OrderByDescending(record => record.OccurredAt)
            .FirstAsync();
        Assert.Equal(before + 1, await assertionScope.ServiceProvider.GetRequiredService<NorthstarDbContext>().AuditRecords.CountAsync());
        Assert.Equal("claim.intake", audit.Action);
        Assert.Equal("audit-test-correlation", audit.CorrelationId);
        Assert.Equal(64, audit.AfterHash.Length);
    }

    [Fact]
    public async Task HealthEndpoints_WithoutAuthentication_ReturnHealthy()
    {
        using var client = factory.CreateClient();

        var live = await client.GetAsync("/health/live");
        var ready = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    private static async Task<string> GetTokenAsync(HttpClient client, params string[] scopes)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/token", new { subject = "test-user", scopes });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("accessToken").GetString()!;
    }
}
