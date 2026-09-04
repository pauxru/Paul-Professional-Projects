using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentPlatform.Api.Security;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPlatform.IntegrationTests;

/// <summary>
/// End-to-end HTTP tests against the real API: authentication (401), scope policies (403), the run
/// lifecycle (start → get → trace), the tool allow-list, ProblemDetails validation and the full
/// refund approval flow over HTTP. All offline against the deterministic mock model.
/// </summary>
public sealed class ApiEndpointsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public ApiEndpointsTests(ApiFactory factory) => _factory = factory;

    private HttpClient AnonymousClient() => _factory.CreateClient();

    private HttpClient ClientWith(params string[] scopes)
    {
        var client = _factory.CreateClient();
        var tokens = _factory.Services.GetRequiredService<TokenService>();
        var jwt = tokens.Issue("test-user", "tenant-alpha", scopes, TimeSpan.FromHours(1));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return client;
    }

    [Fact]
    public async Task Starting_a_run_without_a_token_is_401()
    {
        var client = AnonymousClient();
        var response = await client.PostAsJsonAsync("/api/v1/runs",
            new { workflowName = "support-ticket-triage", inputs = new { ticket_id = "TCK-1001" } });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Listing_approvals_without_the_approve_scope_is_403()
    {
        var client = ClientWith(AgentPolicies.Run); // run scope only, no approve
        var response = await client.GetAsync("/api/v1/approvals");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Tools_endpoint_returns_the_closed_allowlist()
    {
        var client = ClientWith(AgentPolicies.Run);
        var response = await client.GetAsync("/api/v1/tools");
        response.EnsureSuccessStatusCode();

        var tools = await response.Content.ReadFromJsonAsync<JsonElement>();
        var names = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Equal(9, names.Count);
        Assert.Contains("create_refund_request", names);
        Assert.Contains("send_email", names);
        Assert.Contains("http_get", names);
        Assert.Contains("calculate", names);
    }

    [Fact]
    public async Task Workflows_endpoint_lists_the_three_seeded_workflows()
    {
        var client = ClientWith(AgentPolicies.Run);
        var response = await client.GetAsync("/api/v1/workflows");
        response.EnsureSuccessStatusCode();

        var workflows = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(workflows.GetArrayLength() >= 3);
    }

    [Fact]
    public async Task Triage_run_can_be_started_fetched_and_traced()
    {
        var client = ClientWith(AgentPolicies.Run);

        var start = await client.PostAsJsonAsync("/api/v1/runs",
            new { workflowName = "support-ticket-triage", inputs = new { ticket_id = "TCK-1001" } });
        Assert.Equal(HttpStatusCode.Created, start.StatusCode);

        var created = await start.Content.ReadFromJsonAsync<JsonElement>();
        var runId = created.GetProperty("id").GetString()!;
        Assert.Equal("Completed", created.GetProperty("status").GetString());

        var get = await client.GetAsync($"/api/v1/runs/{runId}");
        get.EnsureSuccessStatusCode();

        var trace = await client.GetAsync($"/api/v1/runs/{runId}/trace");
        trace.EnsureSuccessStatusCode();
        var traceBody = await trace.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(traceBody.GetProperty("events").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Starting_a_run_without_a_workflow_name_is_problem_400()
    {
        var client = ClientWith(AgentPolicies.Run);
        var response = await client.PostAsJsonAsync("/api/v1/runs", new { inputs = new { ticket_id = "TCK-1001" } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("application/problem+json", response.Content.Headers.ContentType?.MediaType ?? "");
    }

    [Fact]
    public async Task Unknown_run_returns_404()
    {
        var client = ClientWith(AgentPolicies.Run);
        var response = await client.GetAsync("/api/v1/runs/run-does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Dev_token_endpoint_mints_a_bearer_token()
    {
        var client = AnonymousClient();
        var response = await client.PostAsJsonAsync("/api/v1/dev/token",
            new { subject = "alice", tenant = "tenant-alpha", scopes = new[] { "agents:run" } });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("access_token").GetString()));
    }

    [Fact]
    public async Task Refund_approval_flow_end_to_end_over_http()
    {
        var client = ClientWith(AgentPolicies.Run, AgentPolicies.Approve);

        var start = await client.PostAsJsonAsync("/api/v1/runs", new
        {
            workflowName = "refund-approval",
            inputs = new
            {
                customer_id = "CUST-001",
                order_amount = 50,
                currency = "USD",
                days_since_purchase = 10,
                reason_category = "change_of_mind",
                item_returned = true,
            },
        });
        Assert.Equal(HttpStatusCode.Created, start.StatusCode);
        var created = await start.Content.ReadFromJsonAsync<JsonElement>();
        var runId = created.GetProperty("id").GetString()!;
        Assert.Equal("WaitingForApproval", created.GetProperty("status").GetString());

        var approvals = await client.GetFromJsonAsync<JsonElement>("/api/v1/approvals");
        var approval = approvals.EnumerateArray().Single(a => a.GetProperty("runId").GetString() == runId);
        var approvalId = approval.GetProperty("id").GetString()!;

        var approve = await client.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/approve", new { notes = "ok" });
        approve.EnsureSuccessStatusCode();

        var run = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}");
        Assert.Equal("Completed", run.GetProperty("status").GetString());
        Assert.Equal("Succeeded", run.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Health_endpoint_is_anonymous_and_ok()
    {
        var client = AnonymousClient();
        var response = await client.GetAsync("/health");
        response.EnsureSuccessStatusCode();
    }
}
