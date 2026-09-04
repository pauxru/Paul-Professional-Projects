using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using ReconEngine.IntegrationTests.Support;

namespace ReconEngine.IntegrationTests.Api;

/// <summary>Black-box HTTP tests for the API surface: health, auth, RBAC, import validation.</summary>
public sealed class ApiEndpointTests
{
    private const string SmallInternalCsv =
        "TransactionId,MerchantOrderId,Amount,Currency,TransactionDate,Status\r\n" +
        "TXN_000000001,MOID_000000001,100.00,KES,2024-01-15 12:00:00,Captured\r\n" +
        "TXN_000000002,MOID_000000002,250.50,USD,2024-01-15 12:00:00,Captured\r\n";

    [Fact]
    public async Task Health_endpoint_is_public_and_healthy()
    {
        using var factory = new ReconApiFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_probe_reports_the_database_is_reachable()
    {
        using var factory = new ReconApiFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Token_endpoint_issues_a_jwt_anonymously()
    {
        using var factory = new ReconApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/token",
            new { subject = "alice", scopes = new[] { "recon:run" } });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<TokenBody>();
        Assert.False(string.IsNullOrWhiteSpace(body!.AccessToken));
    }

    [Fact]
    public async Task Protected_endpoint_returns_401_without_a_token()
    {
        using var factory = new ReconApiFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/runs");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Starting_a_run_without_the_run_scope_returns_403()
    {
        using var factory = new ReconApiFactory();
        var client = await factory.CreateAuthedClientAsync("recon:resolve"); // wrong scope

        var response = await client.PostAsJsonAsync("/api/v1/runs", new { ruleSetId = (Guid?)null });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Starting_a_run_with_the_run_scope_succeeds()
    {
        using var factory = new ReconApiFactory();
        var client = await factory.CreateAuthedClientAsync("recon:run");

        // Empty working set is valid: 0 matches, 0 exceptions, balance holds -> run is created.
        var response = await client.PostAsJsonAsync("/api/v1/runs", new { ruleSetId = (Guid?)null });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Import_rejects_a_non_multipart_body()
    {
        using var factory = new ReconApiFactory();
        var client = await factory.CreateAuthedClientAsync("recon:run");

        var response = await client.PostAsJsonAsync("/api/v1/imports", new { nothing = true });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Import_rejects_an_unknown_profile()
    {
        using var factory = new ReconApiFactory();
        var client = await factory.CreateAuthedClientAsync("recon:run");

        using var form = new MultipartFormDataContent { { new StringContent("no-such-profile"), "profile" } };
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(SmallInternalCsv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "internal.csv");

        var response = await client.PostAsync("/api/v1/imports", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Import_happy_path_accepts_all_rows()
    {
        using var factory = new ReconApiFactory();
        var client = await factory.CreateAuthedClientAsync("recon:run");

        using var form = new MultipartFormDataContent { { new StringContent("internal-csv"), "profile" } };
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(SmallInternalCsv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "internal.csv");

        var response = await client.PostAsync("/api/v1/imports", form);

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ImportBody>();
        Assert.Equal(2, result!.TotalRows);
        Assert.Equal(2, result.AcceptedRows);
        Assert.Equal(0, result.RejectedRows);
    }

    private sealed record TokenBody(string AccessToken, DateTime ExpiresAtUtc, string TokenType);

    private sealed record ImportBody(Guid BatchId, int TotalRows, int AcceptedRows, int RejectedRows, string FileChecksum);
}
