using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FieldOps.Application;
using FieldOps.Domain;

namespace FieldOps.UnitTests;

public sealed class BillingWebhookTests
{
    private const string Secret = "test-webhook-secret-with-sufficient-entropy";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-03T00:00:00Z");

    [Fact]
    public async Task Process_ValidPaymentFailure_MarksTenantPastDue()
    {
        var tenant = CreateTenant();
        var processor = CreateProcessor(tenant);
        var webhook = Sign(new BillingWebhookMessage("evt-1", BillingEventType.PaymentFailed, tenant.Id), Now);
        Assert.True(await processor.ProcessAsync(webhook.Body, webhook.Signature, default));
        Assert.Equal(OrganizationStatus.PastDue, tenant.Status);
        Assert.Equal(1, tenant.ConsecutivePaymentFailures);
    }

    [Fact]
    public async Task Process_InvalidSignature_IsRejected()
    {
        var tenant = CreateTenant();
        var processor = CreateProcessor(tenant);
        var webhook = Sign(new BillingWebhookMessage("evt-2", BillingEventType.PaymentFailed, tenant.Id), Now);
        await Assert.ThrowsAsync<ForbiddenOperationException>(
            () => processor.ProcessAsync(webhook.Body, webhook.Signature + "00", default));
        Assert.Equal(OrganizationStatus.Trial, tenant.Status);
    }

    [Fact]
    public async Task Process_ExpiredTimestamp_IsRejected()
    {
        var tenant = CreateTenant();
        var processor = CreateProcessor(tenant);
        var webhook = Sign(
            new BillingWebhookMessage("evt-3", BillingEventType.PaymentFailed, tenant.Id),
            Now.AddMinutes(-6));
        await Assert.ThrowsAsync<ForbiddenOperationException>(
            () => processor.ProcessAsync(webhook.Body, webhook.Signature, default));
    }

    [Fact]
    public async Task Process_ReplayedEvent_IsIdempotent()
    {
        var tenant = CreateTenant();
        var processor = CreateProcessor(tenant);
        var webhook = Sign(new BillingWebhookMessage("evt-replay", BillingEventType.PaymentFailed, tenant.Id), Now);
        Assert.True(await processor.ProcessAsync(webhook.Body, webhook.Signature, default));
        Assert.False(await processor.ProcessAsync(webhook.Body, webhook.Signature, default));
        Assert.Equal(1, tenant.ConsecutivePaymentFailures);
    }

    [Fact]
    public async Task Dunning_ThirdFailure_SuspendsTenant()
    {
        var tenant = CreateTenant();
        var processor = CreateProcessor(tenant);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var webhook = Sign(
                new BillingWebhookMessage($"evt-fail-{attempt}", BillingEventType.PaymentFailed, tenant.Id),
                Now);
            await processor.ProcessAsync(webhook.Body, webhook.Signature, default);
        }

        Assert.Equal(OrganizationStatus.Suspended, tenant.Status);
        Assert.Equal(3, tenant.ConsecutivePaymentFailures);
    }

    [Fact]
    public async Task Dunning_PaymentSuccess_ReactivatesTenant()
    {
        var tenant = CreateTenant();
        var processor = CreateProcessor(tenant);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var failed = Sign(
                new BillingWebhookMessage($"evt-f-{attempt}", BillingEventType.PaymentFailed, tenant.Id),
                Now);
            await processor.ProcessAsync(failed.Body, failed.Signature, default);
        }

        var succeeded = Sign(new BillingWebhookMessage("evt-success", BillingEventType.PaymentSucceeded, tenant.Id), Now);
        await processor.ProcessAsync(succeeded.Body, succeeded.Signature, default);
        Assert.Equal(OrganizationStatus.Active, tenant.Status);
        Assert.Equal(0, tenant.ConsecutivePaymentFailures);
    }

    [Fact]
    public async Task Process_InvoiceEvent_RecordsWithoutChangingStatus()
    {
        var tenant = CreateTenant();
        var processor = CreateProcessor(tenant);
        var webhook = Sign(new BillingWebhookMessage("evt-invoice", BillingEventType.InvoiceGenerated, tenant.Id), Now);
        Assert.True(await processor.ProcessAsync(webhook.Body, webhook.Signature, default));
        Assert.Equal(OrganizationStatus.Trial, tenant.Status);
    }

    private static Organization CreateTenant() =>
        new(Guid.NewGuid(), "billing-test", "Billing Test (fictional)", "KE", SubscriptionPlan.Starter, Now);

    private static BillingWebhookProcessor CreateProcessor(Organization organization) =>
        new(
            new FakeClock(Now),
            new MemoryReceiptStore(),
            new MemoryOrganizationRepository(organization),
            Secret,
            TimeSpan.FromMinutes(5),
            3);

    private static (string Body, string Signature) Sign(BillingWebhookMessage message, DateTimeOffset timestamp)
    {
        var body = JsonSerializer.Serialize(message, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var unix = timestamp.ToUnixTimeSeconds();
        var signature = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(Secret),
            Encoding.UTF8.GetBytes($"{unix}.{body}"));
        return (body, $"t={unix},v1={Convert.ToHexString(signature).ToLowerInvariant()}");
    }
}
