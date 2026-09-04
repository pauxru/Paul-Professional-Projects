using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReconEngine.IntegrationTests.Support;

namespace ReconEngine.IntegrationTests.Workflow;

/// <summary>
/// Exercises the manual-resolution workflow end-to-end through the real HTTP API and the EF Core +
/// SQLite persistence layer: run -> list -> assign -> comment -> resolve, plus the four-eyes write-off
/// approval gate. These tests pin the persistence path that pure-domain unit tests cannot reach — a new
/// audit entry / comment appended to an already-tracked aggregate must be INSERTed, not mistaken for an
/// existing row and UPDATEd (which previously failed the optimistic-concurrency check on every
/// transition). See AppDbContext's ValueGeneratedNever convention for the fix.
/// </summary>
public sealed class ExceptionWorkflowApiTests
{
    private const string Header = "TransactionId,MerchantOrderId,Amount,Currency,TransactionDate,Status\r\n";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static async Task<HttpClient> ClientForAsync(ReconApiFactory factory, string subject, params string[] scopes)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/token", new { subject, scopes });
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenDto>(Json);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return client;
    }

    private static async Task ImportInternalAsync(HttpClient client, string csvBody)
    {
        using var form = new MultipartFormDataContent { { new StringContent("internal-csv"), "profile" } };
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(Header + csvBody));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "internal.csv");
        var response = await client.PostAsync("/api/v1/imports", form);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<ExItem> SingleOpenExceptionAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/exceptions?status=Open&pageSize=50");
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<Paged>(Json);
        return Assert.Single(page!.Items);
    }

    [Fact]
    public async Task Assign_comment_resolve_persists_through_the_api()
    {
        using var factory = new ReconApiFactory();
        var maker = await ClientForAsync(factory, "maker@test", "recon:run", "recon:resolve", "recon:approve");

        // An internal row with no external counterpart yields exactly one MissingInExternal exception.
        await ImportInternalAsync(maker, "TXN_000000001,MOID_000000001,100.00,USD,2024-01-15 12:00:00,Captured\r\n");
        var run = await maker.PostAsJsonAsync("/api/v1/runs", new { ruleSetId = (Guid?)null });
        Assert.Equal(HttpStatusCode.Created, run.StatusCode);

        var ex = await SingleOpenExceptionAsync(maker);
        Assert.Equal("MissingInExternal", ex.Type);

        var assign = await maker.PostAsJsonAsync($"/api/v1/exceptions/{ex.Id}/assign", new { assignee = "maker@test" });
        Assert.Equal(HttpStatusCode.OK, assign.StatusCode);
        Assert.Equal("Assigned", (await assign.Content.ReadFromJsonAsync<ExItem>(Json))!.Status);

        var comment = await maker.PostAsJsonAsync($"/api/v1/exceptions/{ex.Id}/comment", new { text = "Investigated; matching by hand." });
        Assert.Equal(HttpStatusCode.OK, comment.StatusCode);

        var resolve = await maker.PostAsJsonAsync($"/api/v1/exceptions/{ex.Id}/resolve", new { reason = "ManualMatch", note = "matched to bank feed" });
        Assert.Equal(HttpStatusCode.OK, resolve.StatusCode);
        Assert.Equal("Resolved", (await resolve.Content.ReadFromJsonAsync<ExItem>(Json))!.Status);

        // Re-read from a fresh request to prove the transitions were actually committed.
        var reloaded = await maker.GetFromJsonAsync<ExItem>($"/api/v1/exceptions/{ex.Id}", Json);
        Assert.Equal("Resolved", reloaded!.Status);
        Assert.Equal("maker@test", reloaded.AssignedTo);
    }

    [Fact]
    public async Task Write_off_above_threshold_requires_four_eyes_from_a_different_reviewer()
    {
        using var factory = new ReconApiFactory();
        var maker = await ClientForAsync(factory, "maker@test", "recon:run", "recon:resolve", "recon:approve");
        var checker = await ClientForAsync(factory, "checker@test", "recon:approve");

        // 5000.00 USD (500,000 minor) is at/above the 1000.00 write-off approval threshold.
        await ImportInternalAsync(maker, "TXN_000000009,MOID_000000009,5000.00,USD,2024-01-15 12:00:00,Captured\r\n");
        (await maker.PostAsJsonAsync("/api/v1/runs", new { ruleSetId = (Guid?)null })).EnsureSuccessStatusCode();

        var ex = await SingleOpenExceptionAsync(maker);
        Assert.Equal(500_000, ex.AmountMinor);

        // Maker proposes the write-off -> parks in PendingApproval (four-eyes).
        var propose = await maker.PostAsJsonAsync($"/api/v1/exceptions/{ex.Id}/resolve", new { reason = "WriteOff", note = "irrecoverable residual" });
        Assert.Equal(HttpStatusCode.OK, propose.StatusCode);
        Assert.Equal("PendingApproval", (await propose.Content.ReadFromJsonAsync<ExItem>(Json))!.Status);

        // The proposer cannot approve their own write-off.
        var selfApprove = await maker.PostAsync($"/api/v1/exceptions/{ex.Id}/approve", content: null);
        Assert.Equal(HttpStatusCode.Conflict, selfApprove.StatusCode);

        // A different reviewer approves -> Resolved.
        var approve = await checker.PostAsync($"/api/v1/exceptions/{ex.Id}/approve", content: null);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        var resolved = await approve.Content.ReadFromJsonAsync<ExItem>(Json);
        Assert.Equal("Resolved", resolved!.Status);
        Assert.Equal("checker@test", resolved.ApprovedBy);
    }

    private sealed record TokenDto(string AccessToken, DateTime ExpiresAtUtc, string TokenType);

    private sealed record ExItem(
        Guid Id, string Type, string Status, string Currency, long AmountMinor,
        string? AssignedTo, string? ResolvedBy, string? ApprovedBy);

    private sealed record Paged(List<ExItem> Items, int Page, int PageSize, int TotalCount);
}
