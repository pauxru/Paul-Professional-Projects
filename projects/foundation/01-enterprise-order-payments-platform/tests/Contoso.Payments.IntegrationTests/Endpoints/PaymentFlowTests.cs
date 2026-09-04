using System.Net;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Contoso.Payments.Application.Abstractions;
using Contoso.Payments.IntegrationTests.Fixture;

namespace Contoso.Payments.IntegrationTests.Endpoints;

public class PaymentFlowTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PaymentFlowTests(ApiFactory f) { _factory = f; }

    private ApiClient NewClient()
    {
        var http = _factory.CreateClient();
        var token = _factory.IssueToken("orders:write");
        return new ApiClient(http, token);
    }

    private async Task<Guid> PlaceOrder(ApiClient c, string sku, string customerRef = "cust", string currency = "USD", int qty = 1)
    {
        var body = new { customerRef, currency, lines = new[] { new { sku, quantity = qty } } };
        var order = await c.PostOk<JsonElement>("/api/v1/orders", body, Guid.NewGuid().ToString());
        return order.GetProperty("id").GetGuid();
    }

    private async Task<string> GetSku(ApiClient c, string currency = "USD")
    {
        // Deterministic — TEST-USD-1 has 100 units of $10 stock; TEST-KES-1 in KES.
        return currency == "USD" ? "TEST-USD-1" : "TEST-KES-1";
    }

    [Fact]
    public async Task Authorize_same_key_twice_only_charges_once()
    {
        var c = NewClient();
        var sku = await GetSku(c);
        var orderId = await PlaceOrder(c, sku);

        var payKey = Guid.NewGuid().ToString();
        // Ensure simulator returns success deterministically.
        _factory.Simulator.ForcedOutcomes[payKey] = PaymentProviderOutcome.Succeeded;

        var body = new { orderId };
        var first = await c.PostOk<JsonElement>("/api/v1/payments/authorize", body, payKey);
        var second = await c.PostOk<JsonElement>("/api/v1/payments/authorize", body, payKey);

        Assert.Equal(first.GetProperty("id").GetGuid(), second.GetProperty("id").GetGuid());

        // The simulator saw at most one call — the second was replayed by the middleware.
        var attempts = _factory.Simulator.AuthorizeAttempts.TryGetValue(payKey, out var n) ? n : 0;
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Authorize_same_key_different_body_returns_409()
    {
        var c = NewClient();
        var sku = await GetSku(c);
        var orderId1 = await PlaceOrder(c, sku);
        var orderId2 = await PlaceOrder(c, sku, customerRef: "cust-2");

        var payKey = Guid.NewGuid().ToString();
        _factory.Simulator.ForcedOutcomes[payKey] = PaymentProviderOutcome.Succeeded;

        await c.PostOk<JsonElement>("/api/v1/payments/authorize", new { orderId = orderId1 }, payKey);
        var resp = await c.PostRaw("/api/v1/payments/authorize", new { orderId = orderId2 }, payKey);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Provider_timeout_leaves_order_awaiting_payment_and_retry_recovers()
    {
        var c = NewClient();
        var sku = await GetSku(c);
        var orderId = await PlaceOrder(c, sku, customerRef: "timeout-user");

        var payKey = Guid.NewGuid().ToString();
        _factory.Simulator.ForcedOutcomes[payKey] = PaymentProviderOutcome.Timeout;
        _factory.Simulator.ForcedTimeoutRecoveryAttempt = null; // never recover in-band

        var body = new { orderId };
        var first = await c.PostOk<JsonElement>("/api/v1/payments/authorize", body, payKey);
        var intentId = first.GetProperty("id").GetGuid();
        Assert.Equal("Requires", first.GetProperty("status").GetString());

        // Now allow recovery on the very next attempt.
        _factory.Simulator.ForcedTimeoutRecoveryAttempt = 1;
        var retryResp = await c.PostRaw($"/api/v1/payments/{intentId}/retry-authorize", new { });
        Assert.True(retryResp.IsSuccessStatusCode);
        var retried = JsonSerializer.Deserialize<JsonElement>(await retryResp.Content.ReadAsStringAsync());
        Assert.Equal("Authorized", retried.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Authorize_then_capture_then_partial_refund_matches_domain_math()
    {
        var c = NewClient();
        var sku = await GetSku(c);
        var orderId = await PlaceOrder(c, sku, customerRef: "cap-user");

        var payKey = Guid.NewGuid().ToString();
        _factory.Simulator.ForcedOutcomes[payKey] = PaymentProviderOutcome.Succeeded;
        var auth = await c.PostOk<JsonElement>("/api/v1/payments/authorize", new { orderId }, payKey);
        var intentId = auth.GetProperty("id").GetGuid();

        var cap = await c.PostOk<JsonElement>($"/api/v1/payments/{intentId}/capture", new { });
        Assert.Equal("Captured", cap.GetProperty("status").GetString());

        var refund = await c.PostOk<JsonElement>("/api/v1/refunds",
            new { orderId, amount = 3.00m, reason = "customer request" }, Guid.NewGuid().ToString());
        Assert.Equal(3.00m, refund.GetProperty("amount").GetDecimal());

        // Order should be PartiallyRefunded now.
        var order = await c.GetOk<JsonElement>($"/api/v1/orders/{orderId}");
        Assert.Equal("PartiallyRefunded", order.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Refund_exceeding_captured_amount_returns_422()
    {
        var c = NewClient();
        var sku = await GetSku(c);
        var orderId = await PlaceOrder(c, sku, customerRef: "over-user");
        var payKey = Guid.NewGuid().ToString();
        _factory.Simulator.ForcedOutcomes[payKey] = PaymentProviderOutcome.Succeeded;
        var auth = await c.PostOk<JsonElement>("/api/v1/payments/authorize", new { orderId }, payKey);
        var intentId = auth.GetProperty("id").GetGuid();
        await c.PostOk<JsonElement>($"/api/v1/payments/{intentId}/capture", new { });

        var resp = await c.PostRaw("/api/v1/refunds",
            new { orderId, amount = 999.99m, reason = "impossible" }, Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task Second_partial_refund_exceeding_remainder_is_rejected()
    {
        var c = NewClient();
        var sku = await GetSku(c);
        var orderId = await PlaceOrder(c, sku, customerRef: "over-user-2");
        var payKey = Guid.NewGuid().ToString();
        _factory.Simulator.ForcedOutcomes[payKey] = PaymentProviderOutcome.Succeeded;
        var auth = await c.PostOk<JsonElement>("/api/v1/payments/authorize", new { orderId }, payKey);
        var intentId = auth.GetProperty("id").GetGuid();
        await c.PostOk<JsonElement>($"/api/v1/payments/{intentId}/capture", new { });

        // Product is $10.00; first partial refund of $7 leaves $3 remainder.
        await c.PostOk<JsonElement>("/api/v1/refunds",
            new { orderId, amount = 7.00m, reason = "first" }, Guid.NewGuid().ToString());
        var resp = await c.PostRaw("/api/v1/refunds",
            new { orderId, amount = 5.00m, reason = "over remainder" }, Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }
}
