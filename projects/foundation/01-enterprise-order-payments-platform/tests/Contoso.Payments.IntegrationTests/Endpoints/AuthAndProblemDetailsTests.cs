using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Contoso.Payments.IntegrationTests.Fixture;

namespace Contoso.Payments.IntegrationTests.Endpoints;

public class AuthAndProblemDetailsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AuthAndProblemDetailsTests(ApiFactory f) { _factory = f; }

    [Fact]
    public async Task Unauthenticated_POST_orders_returns_401()
    {
        var http = _factory.CreateClient();
        var body = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        var resp = await http.PostAsync("/api/v1/orders", body);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Wrong_scope_returns_403()
    {
        var http = _factory.CreateClient();
        var token = _factory.IssueToken("reconciliation:run"); // does NOT have orders:write
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var products = await http.GetAsync("/api/v1/products");
        var elem = JsonSerializer.Deserialize<JsonElement>(await products.Content.ReadAsStringAsync());
        var sku = elem.GetProperty("items")[0].GetProperty("sku").GetString();
        var body = new StringContent(
            JsonSerializer.Serialize(new { customerRef = "x", currency = "USD", lines = new[] { new { sku, quantity = 1 } } }),
            System.Text.Encoding.UTF8, "application/json");
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders") { Content = body };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Pagination_defaults_are_applied()
    {
        var http = _factory.CreateClient();
        var resp = await http.GetAsync("/api/v1/products?page=1&pageSize=2");
        Assert.True(resp.IsSuccessStatusCode);
        var elem = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        Assert.True(elem.GetProperty("items").GetArrayLength() <= 2);
        Assert.Equal(1, elem.GetProperty("page").GetInt32());
    }

    [Fact]
    public async Task Correlation_id_echoed_back()
    {
        var http = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        var expected = "test-corr-" + Guid.NewGuid().ToString("N")[..8];
        req.Headers.Add("X-Correlation-Id", expected);
        var resp = await http.SendAsync(req);
        Assert.True(resp.IsSuccessStatusCode);
        Assert.Contains(resp.Headers, h => h.Key.Equals("X-Correlation-Id", StringComparison.OrdinalIgnoreCase)
            && h.Value.Contains(expected));
    }

    [Fact]
    public async Task Health_endpoints_report_healthy()
    {
        var http = _factory.CreateClient();
        var live = await http.GetAsync("/health/live");
        var ready = await http.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task Invalid_order_currency_returns_ProblemDetails()
    {
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.IssueToken("orders:write"));
        var body = new StringContent(
            JsonSerializer.Serialize(new { customerRef = "x", currency = "EUR", lines = new[] { new { sku = "TEST-USD-1", quantity = 1 } } }),
            System.Text.Encoding.UTF8, "application/json");
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders") { Content = body };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var resp = await http.SendAsync(req);
        Assert.False(resp.IsSuccessStatusCode);
        var elem = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        Assert.True(elem.TryGetProperty("type", out _));
        Assert.True(elem.TryGetProperty("title", out _));
        Assert.True(elem.TryGetProperty("status", out _));
    }
}
