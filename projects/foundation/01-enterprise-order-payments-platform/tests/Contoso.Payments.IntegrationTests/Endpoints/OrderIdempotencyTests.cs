using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Contoso.Payments.IntegrationTests.Fixture;

namespace Contoso.Payments.IntegrationTests.Endpoints;

public class OrderIdempotencyTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public OrderIdempotencyTests(ApiFactory f) { _factory = f; }

    private ApiClient NewClient()
    {
        var http = _factory.CreateClient();
        var token = _factory.IssueToken("orders:write");
        return new ApiClient(http, token);
    }

    [Fact]
    public async Task Same_key_same_body_returns_same_response()
    {
        var c = NewClient();
        var sku = "TEST-USD-1";
        var key = Guid.NewGuid().ToString();
        var body = new { customerRef = "cust-1", currency = "USD", lines = new[] { new { sku, quantity = 1 } } };

        var first = await c.PostOk<JsonElement>("/api/v1/orders", body, key);
        var second = await c.PostOk<JsonElement>("/api/v1/orders", body, key);
        Assert.Equal(first.GetProperty("id").GetString(), second.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Same_key_different_body_returns_409()
    {
        var c = NewClient();
        var sku = "TEST-USD-1";
        var key = Guid.NewGuid().ToString();
        var bodyA = new { customerRef = "cust-A", currency = "USD", lines = new[] { new { sku, quantity = 1 } } };
        var bodyB = new { customerRef = "cust-B", currency = "USD", lines = new[] { new { sku, quantity = 1 } } };

        await c.PostOk<JsonElement>("/api/v1/orders", bodyA, key);
        var resp = await c.PostRaw("/api/v1/orders", bodyB, key);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var problem = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        Assert.Equal("idempotency.conflict", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Missing_idempotency_key_returns_400()
    {
        var c = NewClient();
        var sku = "TEST-USD-1";
        var body = new { customerRef = "cust-noidem", currency = "USD", lines = new[] { new { sku, quantity = 1 } } };
        var resp = await c.PostRaw("/api/v1/orders", body, idempotencyKey: null);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Replay_returns_Idempotent_Replay_header()
    {
        var c = NewClient();
        var sku = "TEST-USD-1";
        var key = Guid.NewGuid().ToString();
        var body = new { customerRef = "cust-hdr", currency = "USD", lines = new[] { new { sku, quantity = 1 } } };
        var first = await c.PostRaw("/api/v1/orders", body, key);
        var second = await c.PostRaw("/api/v1/orders", body, key);
        Assert.True(first.IsSuccessStatusCode);
        Assert.True(second.IsSuccessStatusCode);
        Assert.True(second.Headers.Contains("Idempotent-Replay"));
    }
}
