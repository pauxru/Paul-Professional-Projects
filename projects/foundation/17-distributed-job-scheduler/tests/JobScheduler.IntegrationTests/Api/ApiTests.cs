using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace JobScheduler.IntegrationTests.Api;

/// <summary>
/// HTTP-level API tests: authentication (401), scope-based authorization (403), request validation
/// (400/409), health probes, and the create -> trigger -> list happy path. Every request is bounded
/// by a hard timeout so the suite always terminates.
/// </summary>
public sealed class ApiTests(SchedulerApiFactory factory) : IClassFixture<SchedulerApiFactory>
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private readonly SchedulerApiFactory _factory = factory;

    private static CancellationToken Ct() => new CancellationTokenSource(Deadline).Token;

    private async Task<HttpClient> ClientWithScopesAsync(params string[] scopes)
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/token", new { subject = "tester", scopes }, Ct());
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(Ct()));
        var token = doc.RootElement.GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static object NewJob(string name, string handler = "report-generator", string payload = "{}") => new
    {
        name,
        handlerType = handler,
        payloadJson = payload
    };

    [Fact]
    public async Task Health_live_is_anonymous_and_ok()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/health/live", Ct());
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Health_ready_reports_ok()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/health/ready", Ct());
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(Ct()));
        Assert.Equal("ready", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Listing_jobs_without_a_token_is_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/jobs", Ct());
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Read_scope_can_list_but_cannot_create_403()
    {
        var client = await ClientWithScopesAsync("jobs:read");

        var list = await client.GetAsync("/api/v1/jobs", Ct());
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        var create = await client.PostAsJsonAsync("/api/v1/jobs", NewJob($"read-denied-{Guid.NewGuid():N}"), Ct());
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }

    [Fact]
    public async Task Manage_scope_can_create_and_fetch_a_job()
    {
        var client = await ClientWithScopesAsync("jobs:manage");
        var name = $"report-{Guid.NewGuid():N}";

        var create = await client.PostAsJsonAsync("/api/v1/jobs", NewJob(name), Ct());
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync(Ct()));
        var id = created.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(name, created.RootElement.GetProperty("name").GetString());

        var get = await client.GetAsync($"/api/v1/jobs/{id}", Ct());
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
    }

    [Fact]
    public async Task Creating_a_job_with_an_unregistered_handler_is_rejected_400()
    {
        var client = await ClientWithScopesAsync("jobs:manage");
        // A payload-driven handler name that is NOT on the allow-list must be refused (no arbitrary code).
        var resp = await client.PostAsJsonAsync("/api/v1/jobs", NewJob($"evil-{Guid.NewGuid():N}", handler: "rm -rf /"), Ct());
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Creating_a_job_with_malformed_payload_json_is_rejected_400()
    {
        var client = await ClientWithScopesAsync("jobs:manage");
        var resp = await client.PostAsJsonAsync("/api/v1/jobs", NewJob($"badjson-{Guid.NewGuid():N}", payload: "{not-json"), Ct());
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Creating_two_jobs_with_the_same_name_conflicts_409()
    {
        var client = await ClientWithScopesAsync("jobs:manage");
        var name = $"dup-{Guid.NewGuid():N}";

        var first = await client.PostAsJsonAsync("/api/v1/jobs", NewJob(name), Ct());
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/v1/jobs", NewJob(name), Ct());
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Trigger_scope_cannot_create_but_can_trigger_a_run()
    {
        // Create the definition with a manage token.
        var manage = await ClientWithScopesAsync("jobs:manage");
        var name = $"triggerable-{Guid.NewGuid():N}";
        var create = await manage.PostAsJsonAsync("/api/v1/jobs", NewJob(name), Ct());
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        using var createdDoc = JsonDocument.Parse(await create.Content.ReadAsStringAsync(Ct()));
        var id = createdDoc.RootElement.GetProperty("id").GetGuid();

        // A trigger-only token cannot create definitions...
        var trigger = await ClientWithScopesAsync("jobs:trigger");
        var deniedCreate = await trigger.PostAsJsonAsync("/api/v1/jobs", NewJob($"nope-{Guid.NewGuid():N}"), Ct());
        Assert.Equal(HttpStatusCode.Forbidden, deniedCreate.StatusCode);

        // ...but it can trigger a run.
        var triggered = await trigger.PostAsJsonAsync($"/api/v1/jobs/{id}/trigger", new { }, Ct());
        Assert.Equal(HttpStatusCode.OK, triggered.StatusCode);
        using var runDoc = JsonDocument.Parse(await triggered.Content.ReadAsStringAsync(Ct()));
        Assert.Equal(id, runDoc.RootElement.GetProperty("jobDefinitionId").GetGuid());
        Assert.Equal("Pending", runDoc.RootElement.GetProperty("state").GetString());

        // The run is visible on the runs list.
        var read = await ClientWithScopesAsync("jobs:read");
        var runs = await read.GetAsync($"/api/v1/runs?jobDefinitionId={id}", Ct());
        Assert.Equal(HttpStatusCode.OK, runs.StatusCode);
        using var runsDoc = JsonDocument.Parse(await runs.Content.ReadAsStringAsync(Ct()));
        Assert.True(runsDoc.RootElement.GetProperty("totalCount").GetInt32() >= 1);
    }

    [Fact]
    public async Task Leader_endpoint_requires_authentication()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/leader", Ct())).StatusCode);

        var read = await ClientWithScopesAsync("jobs:read");
        Assert.Equal(HttpStatusCode.OK, (await read.GetAsync("/api/v1/leader", Ct())).StatusCode);
    }
}
