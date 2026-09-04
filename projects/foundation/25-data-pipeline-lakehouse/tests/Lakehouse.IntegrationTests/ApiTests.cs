using System.Net;
using System.Net.Http.Json;
using Lakehouse.Api.Auth;

namespace Lakehouse.IntegrationTests;

/// <summary>
/// Black-box HTTP tests over the seeded API: anonymous vs authenticated vs role-gated routes, the guarded
/// SQL surface (SELECT ok, write/injection rejected), the anonymous dashboard, lineage and the metrics
/// semantic layer. These exercise the real DI graph, auth pipeline and serving store end-to-end.
/// </summary>
public sealed class ApiTests : IClassFixture<LakehouseAppFactory>
{
    private readonly LakehouseAppFactory _factory;

    public ApiTests(LakehouseAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Health_is_anonymous_and_ok()
    {
        var resp = await _factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Protected_route_without_token_is_401()
    {
        var resp = await _factory.CreateClient().GetAsync("/api/pipeline/dag");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Reader_can_read_the_dag()
    {
        var client = await _factory.ClientForAsync(AuthPolicies.ReaderRole);
        var resp = await client.GetAsync("/api/pipeline/dag");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Reader_is_forbidden_from_operator_run()
    {
        var client = await _factory.ClientForAsync(AuthPolicies.ReaderRole);
        var resp = await client.PostAsJsonAsync("/api/pipeline/run", new { window = "full" });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Sql_select_returns_rows()
    {
        var client = await _factory.ClientForAsync(AuthPolicies.ReaderRole);
        var resp = await client.PostAsJsonAsync("/api/sql", new { sql = "SELECT date_key, revenue_usd FROM agg_daily_revenue" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<QueryResultDto>();
        Assert.NotNull(body);
        Assert.Contains("date_key", body!.Columns);
    }

    [Fact]
    public async Task Sql_write_is_rejected_400()
    {
        var client = await _factory.ClientForAsync(AuthPolicies.ReaderRole);
        var resp = await client.PostAsJsonAsync("/api/sql", new { sql = "UPDATE dim_customer SET name = 'x'" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Sql_injection_via_batching_is_rejected_400()
    {
        var client = await _factory.ClientForAsync(AuthPolicies.ReaderRole);
        var resp = await client.PostAsJsonAsync("/api/sql", new { sql = "SELECT 1; DROP TABLE dim_customer" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Dashboard_funnel_is_anonymous()
    {
        var resp = await _factory.CreateClient().GetAsync("/api/dashboard/funnel");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Lineage_mermaid_renders()
    {
        var client = await _factory.ClientForAsync(AuthPolicies.ReaderRole);
        var resp = await client.GetAsync("/api/lineage/mermaid");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var text = await resp.Content.ReadAsStringAsync();
        Assert.Contains("flowchart LR", text);
    }

    [Fact]
    public async Task Metrics_query_resolves_and_executes()
    {
        var client = await _factory.ClientForAsync(AuthPolicies.ReaderRole);
        var resp = await client.PostAsJsonAsync("/api/metrics/query",
            new { metric = "revenue_usd", dimensions = new[] { "channel" }, grain = "Month" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Metrics_query_unknown_metric_is_rejected_400()
    {
        var client = await _factory.ClientForAsync(AuthPolicies.ReaderRole);
        var resp = await client.PostAsJsonAsync("/api/metrics/query", new { metric = "revenue_kes" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    private sealed record QueryResultDto(string[] Columns, object[][] Rows, bool Truncated, double ElapsedMs);
}
