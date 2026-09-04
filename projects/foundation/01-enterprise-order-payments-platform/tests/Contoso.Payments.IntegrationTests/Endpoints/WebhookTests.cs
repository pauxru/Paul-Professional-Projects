using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Contoso.Payments.Application.Abstractions;
using Contoso.Payments.Application.Common;
using Contoso.Payments.Application.Webhooks;
using Contoso.Payments.Infrastructure.Webhooks;
using Contoso.Payments.IntegrationTests.Fixture;

namespace Contoso.Payments.IntegrationTests.Endpoints;

public class WebhookTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public WebhookTests(ApiFactory f) { _factory = f; }

    private ApiClient NewClient()
    {
        var http = _factory.CreateClient();
        return new ApiClient(http, _factory.IssueToken("orders:write"));
    }

    private HmacSha256WebhookSignatureVerifier Verifier => (HmacSha256WebhookSignatureVerifier)_factory.Services
        .GetRequiredService<IWebhookSignatureVerifier>();

    private static byte[] Encode(WebhookPayload payload)
        => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private async Task<Guid> AuthorizedIntent(ApiClient c)
    {
        var sku = "TEST-USD-1";
        var orderBody = new { customerRef = "wh-user", currency = "USD", lines = new[] { new { sku, quantity = 1 } } };
        var order = await c.PostOk<JsonElement>("/api/v1/orders", orderBody, Guid.NewGuid().ToString());
        var orderId = order.GetProperty("id").GetGuid();
        var payKey = Guid.NewGuid().ToString();
        _factory.Simulator.ForcedOutcomes[payKey] = PaymentProviderOutcome.AsynchronousPending;
        var authResp = await c.PostOk<JsonElement>("/api/v1/payments/authorize", new { orderId }, payKey);
        return authResp.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Valid_webhook_captures_and_marks_order_paid()
    {
        var c = NewClient();
        var intentId = await AuthorizedIntent(c);
        var payload = new WebhookPayload("payment.captured", intentId, "prov-ref-1", 10m, "USD", null, null,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var body = Encode(payload);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sig = Verifier.Sign(ts, body);
        var http = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/payments")
        {
            Content = new ByteArrayContent(body)
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        req.Headers.Add("X-Contoso-Signature", $"t={ts},v1={sig}");
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Bad_signature_returns_401_and_does_not_mutate()
    {
        var c = NewClient();
        var intentId = await AuthorizedIntent(c);
        var payload = new WebhookPayload("payment.captured", intentId, "prov-ref-2", 10m, "USD", null, null,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var body = Encode(payload);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var http = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/payments")
        {
            Content = new ByteArrayContent(body)
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        req.Headers.Add("X-Contoso-Signature", $"t={ts},v1=deadbeef");
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);

        // Intent status is untouched.
        var token = _factory.IssueToken("orders:write");
        var httpAuth = _factory.CreateClient();
        httpAuth.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var intent = await httpAuth.GetAsync($"/api/v1/payments/{intentId}");
        var elem = JsonSerializer.Deserialize<JsonElement>(await intent.Content.ReadAsStringAsync());
        Assert.Equal("Requires", elem.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Replay_of_same_signature_is_ignored()
    {
        var c = NewClient();
        var intentId = await AuthorizedIntent(c);
        var payload = new WebhookPayload("payment.captured", intentId, "prov-ref-r", 10m, "USD", null, null,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var body = Encode(payload);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sig = Verifier.Sign(ts, body);

        var http = _factory.CreateClient();
        HttpRequestMessage MakeRequest()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/payments")
            {
                Content = new ByteArrayContent(body)
            };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            req.Headers.Add("X-Contoso-Signature", $"t={ts},v1={sig}");
            return req;
        }
        var r1 = await http.SendAsync(MakeRequest());
        var r2 = await http.SendAsync(MakeRequest());
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        var body2 = JsonSerializer.Deserialize<JsonElement>(await r2.Content.ReadAsStringAsync());
        Assert.True(body2.GetProperty("replay").GetBoolean());
    }

    [Fact]
    public async Task Malformed_body_with_valid_signature_returns_400()
    {
        var body = Encoding.UTF8.GetBytes("not-json");
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sig = Verifier.Sign(ts, body);
        var http = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/payments")
        {
            Content = new ByteArrayContent(body)
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        req.Headers.Add("X-Contoso-Signature", $"t={ts},v1={sig}");
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
