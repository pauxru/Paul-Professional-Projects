using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Contoso.Payments.Application.Abstractions;
using Contoso.Payments.Application.Common;
using Contoso.Payments.Application.Webhooks;
using Contoso.Payments.Infrastructure.Persistence;
using Contoso.Payments.Infrastructure.Webhooks;
using Contoso.Payments.IntegrationTests.Fixture;

namespace Contoso.Payments.IntegrationTests.Endpoints;

/// <summary>
/// End-to-end async webhook flow: place order → authorize with AsynchronousPending → provider
/// posts a signed webhook → order transitions to Paid and inventory is committed.
/// </summary>
public class AsyncWebhookCaptureFlowTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AsyncWebhookCaptureFlowTests(ApiFactory f) { _factory = f; }

    [Fact]
    public async Task Full_async_capture_lifecycle_via_webhook()
    {
        var http = _factory.CreateClient();
        var token = _factory.IssueToken("orders:write");
        var client = new ApiClient(http, token);

        // 1. Place order.
        var orderKey = Guid.NewGuid().ToString();
        var order = await client.PostOk<JsonElement>("/api/v1/orders", new
        {
            customerRef = "async-capture-user",
            currency = "USD",
            lines = new[] { new { sku = "TEST-USD-1", quantity = 1 } }
        }, orderKey);
        var orderId = order.GetProperty("id").GetGuid();

        // 2. Authorize with async pending — intent stays in Requires; order in AwaitingPayment.
        var payKey = Guid.NewGuid().ToString();
        _factory.Simulator.ForcedOutcomes[payKey] = PaymentProviderOutcome.AsynchronousPending;
        var auth = await client.PostOk<JsonElement>("/api/v1/payments/authorize", new { orderId }, payKey);
        var intentId = auth.GetProperty("id").GetGuid();
        Assert.Equal("Requires", auth.GetProperty("status").GetString());

        // 3. Provider posts a signed webhook with payment.captured.
        var verifier = (HmacSha256WebhookSignatureVerifier)_factory.Services.GetRequiredService<IWebhookSignatureVerifier>();
        var payload = new WebhookPayload("payment.captured", intentId, "prov-async-1", 10m, "USD", null, null,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sig = verifier.Sign(ts, body);
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/payments")
        {
            Content = new ByteArrayContent(body)
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        req.Headers.Add("X-Contoso-Signature", $"t={ts},v1={sig}");
        var webhookResp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, webhookResp.StatusCode);

        // 4. Order should now be Paid.
        var afterOrder = await client.GetOk<JsonElement>($"/api/v1/orders/{orderId}");
        Assert.Equal("Paid", afterOrder.GetProperty("status").GetString());

        // 5. Ledger entry should exist for the capture.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ledgerCount = await db.LedgerEntries.CountAsync(l => l.PaymentIntentId == intentId);
        Assert.True(ledgerCount >= 1);
    }
}
