using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SubscriptionBilling.Api;
using SubscriptionBilling.Application;
using SubscriptionBilling.Domain;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SubscriptionBilling.IntegrationTests;

public sealed class ApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    [Fact]
    public async Task HealthReady_WithInMemorySqlite_ReturnsHealthyAndCorrelationId()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("X-Correlation-Id"));
    }

    [Fact]
    public async Task OpenApi_InTestingEnvironment_ExposesVersionedDocument()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json");
        var document = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("/api/v1/invoices/preview", document, StringComparison.Ordinal);
        Assert.Contains("/api/v1/usage", document, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Products_WithoutToken_Returns401()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/products");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Products_WithReadOnlyTokenOnWrite_Returns403()
    {
        using var client = await AuthenticatedClientAsync("billing.read");
        using var request = JsonRequest(
            HttpMethod.Post,
            "/api/v1/products",
            new CreateProductCommand("Read-only forbidden", "test"),
            $"read-forbidden-{Guid.NewGuid():N}");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Products_BlankName_ReturnsProblemDetailsValidation400()
    {
        using var client = await AuthenticatedClientAsync("billing.write");
        using var request = JsonRequest(
            HttpMethod.Post,
            "/api/v1/products",
            new CreateProductCommand("", "invalid"),
            $"validation-{Guid.NewGuid():N}");

        using var response = await client.SendAsync(request);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Validation failed", json.GetProperty("title").GetString());
        Assert.True(json.TryGetProperty("errors", out _));
        Assert.True(json.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task ProductCreate_RepeatedIdempotencyKey_ReplaysOriginalResponse()
    {
        using var client = await AuthenticatedClientAsync("billing.write");
        var command = new CreateProductCommand(
            $"Idempotent product {Guid.NewGuid():N}",
            "integration test");
        var key = $"product-{Guid.NewGuid():N}";

        using var firstRequest = JsonRequest(HttpMethod.Post, "/api/v1/products", command, key);
        using var first = await client.SendAsync(firstRequest);
        var firstProduct = await ReadAsync<ProductView>(first);
        using var secondRequest = JsonRequest(HttpMethod.Post, "/api/v1/products", command, key);
        using var second = await client.SendAsync(secondRequest);
        var secondProduct = await ReadAsync<ProductView>(second);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(firstProduct.Id, secondProduct.Id);
        Assert.Equal("true", second.Headers.GetValues("Idempotency-Replayed").Single());
    }

    [Fact]
    public async Task InvoicePreview_MidCycleUpgrade_ReturnsReconciledProrationLines()
    {
        var setup = await CreateSubscriptionSetupAsync();
        using var client = await AuthenticatedClientAsync("billing.read");
        var command = new PreviewInvoiceCommand(
            setup.Subscription.Id,
            setup.UpgradeVersionId,
            1,
            new DateTimeOffset(2026, 1, 16, 0, 0, 0, TimeSpan.Zero),
            ProrationBehavior.CreateProrations);
        using var request = JsonRequest(
            HttpMethod.Post,
            "/api/v1/invoices/preview",
            command,
            $"preview-{Guid.NewGuid():N}");

        using var response = await client.SendAsync(request);
        var preview = await ReadAsync<InvoicePreviewView>(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(preview.NetAmountMinor > 0);
        Assert.Equal(preview.NetAmountMinor, preview.Lines.Sum(line => line.AmountMinor));
    }

    [Fact]
    public async Task Usage_RepeatedEventId_IsAcceptedOnce()
    {
        var setup = await CreateSubscriptionSetupAsync(metered: true);
        using var client = await AuthenticatedClientAsync("billing.write");
        var command = new RecordUsageCommand(
            $"usage-{Guid.NewGuid():N}",
            setup.Subscription.Id,
            setup.MeterId!.Value,
            new DateTimeOffset(2026, 1, 20, 0, 0, 0, TimeSpan.Zero),
            12m,
            null,
            null);

        using var first = await client.PostAsJsonAsync("/api/v1/usage", command, JsonOptions);
        var firstReceipt = await ReadAsync<UsageReceipt>(first);
        using var second = await client.PostAsJsonAsync("/api/v1/usage", command, JsonOptions);
        var secondReceipt = await ReadAsync<UsageReceipt>(second);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.False(firstReceipt.Duplicate);
        Assert.True(secondReceipt.Duplicate);
        Assert.Equal(12m, secondReceipt.RollupValue);
    }

    [Fact]
    public async Task Usage_ForClosedPeriod_WithRejectPolicy_Returns422()
    {
        var setup = await CreateSubscriptionSetupAsync(metered: true);
        using var client = await AuthenticatedClientAsync("billing.write");
        var command = new RecordUsageCommand(
            $"late-{Guid.NewGuid():N}",
            setup.Subscription.Id,
            setup.MeterId!.Value,
            new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero),
            1m,
            null,
            null);

        using var response = await client.PostAsJsonAsync("/api/v1/usage", command, JsonOptions);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task InvoiceRun_RepeatedManualTrigger_CreatesOnePeriodicInvoice()
    {
        var setup = await CreateSubscriptionSetupAsync();
        factory.Clock.Set(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
        using var admin = await AuthenticatedClientAsync("billing.admin");

        using var firstRequest = JsonRequest(
            HttpMethod.Post,
            "/api/v1/invoices/run",
            new { },
            $"run-one-{Guid.NewGuid():N}");
        using var first = await admin.SendAsync(firstRequest);
        var firstResult = await ReadAsync<InvoiceRunResult>(first);
        using var secondRequest = JsonRequest(
            HttpMethod.Post,
            "/api/v1/invoices/run",
            new { },
            $"run-two-{Guid.NewGuid():N}");
        using var second = await admin.SendAsync(secondRequest);
        var secondResult = await ReadAsync<InvoiceRunResult>(second);
        using var list = await admin.GetAsync("/api/v1/invoices?page=1&pageSize=100");
        var invoices = await ReadAsync<Page<InvoiceView>>(list);

        Assert.True(firstResult.Generated >= 1);
        Assert.Equal(0, secondResult.Generated);
        _ = Assert.Single(
            invoices.Items,
            item => item.SubscriptionId == setup.Subscription.Id);
    }

    [Fact]
    public async Task PaymentWebhook_InvalidSignatureAndReplay_AreRejected()
    {
        var setup = await CreateSubscriptionAndInvoiceAsync();
        var body = JsonSerializer.Serialize(
            new BillingEndpoints.PaymentWebhookRequest("payment.succeeded", setup.Invoice.Id),
            JsonOptions);
        var signer = new WebhookSignatureService(
            factory.Clock,
            new NoopReplayStore(),
            "dev-only-not-a-real-secret-webhook-key-0123456789",
            TimeSpan.FromMinutes(5));
        var signature = signer.Sign(body, factory.Clock.UtcNow);
        using var client = factory.CreateClient();

        using var invalid = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/payments")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        invalid.Headers.Add("X-Billing-Signature", "t=1,v1=bad");
        invalid.Headers.Add("X-Webhook-Nonce", $"invalid-{Guid.NewGuid():N}");
        using var invalidResponse = await client.SendAsync(invalid);

        var nonce = $"valid-{Guid.NewGuid():N}";
        using var valid = WebhookRequest(body, signature, nonce);
        using var validResponse = await client.SendAsync(valid);
        using var replay = WebhookRequest(body, signature, nonce);
        using var replayResponse = await client.SendAsync(replay);

        Assert.Equal(HttpStatusCode.Unauthorized, invalidResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, validResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, replayResponse.StatusCode);
    }

    [Fact]
    public async Task FailedPaymentThenSignedSuccessWebhook_RecoversSubscription()
    {
        var setup = await CreateSubscriptionAndInvoiceAsync();
        using var write = await AuthenticatedClientAsync("billing.write");
        using var payRequest = JsonRequest(
            HttpMethod.Post,
            $"/api/v1/invoices/{setup.Invoice.Id}/pay",
            new BillingEndpoints.PayInvoiceRequest("pm_insufficient"),
            $"pay-fail-{Guid.NewGuid():N}");
        using var failed = await write.SendAsync(payRequest);
        var stillOpen = await ReadAsync<InvoiceView>(failed);
        Assert.Equal(InvoiceStatus.Open, stillOpen.Status);

        var body = JsonSerializer.Serialize(
            new BillingEndpoints.PaymentWebhookRequest("payment.succeeded", setup.Invoice.Id),
            JsonOptions);
        var signer = new WebhookSignatureService(
            factory.Clock,
            new NoopReplayStore(),
            "dev-only-not-a-real-secret-webhook-key-0123456789",
            TimeSpan.FromMinutes(5));
        using var webhook = WebhookRequest(
            body,
            signer.Sign(body, factory.Clock.UtcNow),
            $"recover-{Guid.NewGuid():N}");
        using var anonymous = factory.CreateClient();
        using var webhookResponse = await anonymous.SendAsync(webhook);
        using var invoiceResponse = await write.GetAsync($"/api/v1/invoices/{setup.Invoice.Id}");
        var invoice = await ReadAsync<InvoiceView>(invoiceResponse);

        Assert.Equal(HttpStatusCode.Accepted, webhookResponse.StatusCode);
        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
    }

    [Fact]
    public async Task InvoiceRender_Html_EncodesAndReturnsDocument()
    {
        var setup = await CreateSubscriptionAndInvoiceAsync();
        using var client = await AuthenticatedClientAsync("billing.read");

        using var response = await client.GetAsync(
            $"/api/v1/invoices/{setup.Invoice.Id}/render?format=html");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<!doctype html>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(setup.Invoice.Number!, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreditNote_ForInvoice_CreatesImmutableAdjustment()
    {
        var setup = await CreateSubscriptionAndInvoiceAsync();
        using var client = await AuthenticatedClientAsync("billing.admin");
        using var request = JsonRequest(
            HttpMethod.Post,
            $"/api/v1/invoices/{setup.Invoice.Id}/credit-notes",
            new CreateCreditNoteCommand(500, "Fictional service adjustment"),
            $"credit-note-{Guid.NewGuid():N}");

        using var response = await client.SendAsync(request);
        var creditNote = await ReadAsync<CreditNoteView>(response);

        Assert.Equal(setup.Invoice.Id, creditNote.InvoiceId);
        Assert.Equal(500, creditNote.AmountMinor);
        Assert.StartsWith("CN-", creditNote.Number, StringComparison.Ordinal);
    }

    private async Task<(SubscriptionView Subscription, Guid UpgradeVersionId, Guid? MeterId)>
        CreateSubscriptionSetupAsync(bool metered = false)
    {
        factory.Clock.Set(new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero));
        using var client = await AuthenticatedClientAsync("billing.write");
        var suffix = Guid.NewGuid().ToString("N");
        var product = await PostAsync<ProductView>(
            client,
            "/api/v1/products",
            new CreateProductCommand($"Product-{suffix}", "integration"),
            $"product-{suffix}");
        Guid? meterId = null;
        if (metered)
        {
            var meter = await PostAsync<MeterView>(
                client,
                "/api/v1/plans/meters",
                new CreateMeterCommand(
                    $"Meter-{suffix}",
                    "request",
                    UsageAggregationMode.Sum,
                    1m,
                    UsageRoundingMode.Up),
                $"meter-{suffix}");
            meterId = meter.Id;
        }

        var plan = await PostAsync<PlanView>(
            client,
            "/api/v1/plans",
            new CreatePlanCommand(
                product.Id,
                $"Plan-{suffix}",
                BillingIntervalUnit.Month,
                1,
                meterId,
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                "USD",
                metered
                    ? new PricingConfiguration(PricingModel.PerUnit, UnitPriceMinor: 100)
                    : new PricingConfiguration(PricingModel.FlatRecurring, FlatFeeMinor: 10_000),
                false),
            $"plan-{suffix}");
        var upgrade = await PostAsync<PlanVersionView>(
            client,
            $"/api/v1/plans/{plan.Id}/versions",
            new AddPlanVersionCommand(
                new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero),
                "USD",
                new PricingConfiguration(
                    metered ? PricingModel.PerUnit : PricingModel.FlatRecurring,
                    FlatFeeMinor: metered ? null : 20_000,
                    UnitPriceMinor: metered ? 150 : null),
                false),
            $"version-{suffix}");
        var customer = await PostAsync<CustomerView>(
            client,
            "/api/v1/customers",
            new CreateCustomerCommand(
                $"Customer-{suffix}",
                "USD",
                "US",
                false,
                false),
            $"customer-{suffix}");
        var subscription = await PostAsync<SubscriptionView>(
            client,
            "/api/v1/subscriptions",
            new CreateSubscriptionCommand(
                customer.Id,
                plan.Versions[0].Id,
                1,
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                null,
                TrialEndBehavior.Activate,
                null),
            $"subscription-{suffix}");
        return (subscription, upgrade.Id, meterId);
    }

    private async Task<(SubscriptionView Subscription, InvoiceView Invoice)>
        CreateSubscriptionAndInvoiceAsync()
    {
        var setup = await CreateSubscriptionSetupAsync();
        factory.Clock.Set(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
        using var client = await AuthenticatedClientAsync("billing.admin");
        using var run = JsonRequest(
            HttpMethod.Post,
            "/api/v1/invoices/run",
            new { },
            $"run-{Guid.NewGuid():N}");
        using var runResponse = await client.SendAsync(run);
        _ = await ReadAsync<InvoiceRunResult>(runResponse);
        using var invoicesResponse = await client.GetAsync("/api/v1/invoices?page=1&pageSize=100");
        var invoices = await ReadAsync<Page<InvoiceView>>(invoicesResponse);
        return (
            setup.Subscription,
            invoices.Items.Single(item => item.SubscriptionId == setup.Subscription.Id));
    }

    private async Task<HttpClient> AuthenticatedClientAsync(params string[] scopes)
    {
        var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/token",
            new BillingEndpoints.TokenRequest($"test-{Guid.NewGuid():N}", scopes),
            JsonOptions);
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token.GetProperty("accessToken").GetString());
        return client;
    }

    private static async Task<T> PostAsync<T>(
        HttpClient client,
        string path,
        object body,
        string idempotencyKey)
    {
        using var request = JsonRequest(HttpMethod.Post, path, body, idempotencyKey);
        using var response = await client.SendAsync(request);
        return await ReadAsync<T>(response);
    }

    private static HttpRequestMessage JsonRequest(
        HttpMethod method,
        string path,
        object body,
        string idempotencyKey)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return request;
    }

    private static HttpRequestMessage WebhookRequest(
        string body,
        string signature,
        string nonce)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/payments")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Billing-Signature", signature);
        request.Headers.Add("X-Webhook-Nonce", nonce);
        return request;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.IsSuccessStatusCode,
            $"Expected success but received {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, JsonOptions)
               ?? throw new InvalidOperationException("Response body was empty.");
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed class NoopReplayStore : IWebhookReplayStore
    {
        public Task<bool> TryRecordAsync(
            string nonce,
            DateTimeOffset expiresAt,
            CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
