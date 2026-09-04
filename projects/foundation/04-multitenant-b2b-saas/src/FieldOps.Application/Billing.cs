using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FieldOps.Domain;

namespace FieldOps.Application;

public sealed class BillingWebhookProcessor(
    IClock clock,
    IWebhookReceiptStore receipts,
    IOrganizationRepository organizations,
    string signingSecret,
    TimeSpan tolerance,
    int suspendAfterFailures)
{
    public async Task<bool> ProcessAsync(string rawBody, string signatureHeader, CancellationToken cancellationToken)
    {
        var timestamp = VerifySignature(rawBody, signatureHeader);
        var message = JsonSerializer.Deserialize<BillingWebhookMessage>(rawBody, JsonOptions)
            ?? throw new DomainRuleException("Webhook payload is invalid.");

        var firstDelivery = await receipts.TryRecordAsync(message.EventId, message.Type.ToString(), clock.UtcNow, cancellationToken);
        if (!firstDelivery) return false;

        var organization = await organizations.FindByIdAsync(message.TenantId, cancellationToken)
            ?? throw new KeyNotFoundException("Webhook tenant was not found.");

        switch (message.Type)
        {
            case BillingEventType.PaymentFailed:
                organization.RecordPaymentFailure(suspendAfterFailures);
                break;
            case BillingEventType.PaymentSucceeded:
                organization.RecordPaymentSuccess();
                break;
        }

        await organizations.SaveAsync(cancellationToken);
        _ = timestamp;
        return true;
    }

    public long VerifySignature(string rawBody, string signatureHeader)
    {
        var parts = signatureHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2))
            .Where(part => part.Length == 2)
            .ToDictionary(part => part[0], part => part[1], StringComparer.Ordinal);
        if (!parts.TryGetValue("t", out var timestampText)
            || !long.TryParse(timestampText, out var timestamp)
            || !parts.TryGetValue("v1", out var suppliedHex))
        {
            throw new ForbiddenOperationException("Webhook signature header is malformed.");
        }

        var signedAt = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        if ((clock.UtcNow - signedAt).Duration() > tolerance)
        {
            throw new ForbiddenOperationException("Webhook signature timestamp is outside the allowed window.");
        }

        byte[] supplied;
        try
        {
            supplied = Convert.FromHexString(suppliedHex);
        }
        catch (FormatException)
        {
            throw new ForbiddenOperationException("Webhook signature is invalid.");
        }

        var expected = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(signingSecret),
            Encoding.UTF8.GetBytes($"{timestamp}.{rawBody}"));
        if (!CryptographicOperations.FixedTimeEquals(expected, supplied))
        {
            throw new ForbiddenOperationException("Webhook signature is invalid.");
        }

        return timestamp;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

public sealed class BillingService(
    ITenantContext tenantContext,
    IOrganizationRepository organizations,
    IBillingProvider provider,
    IClock clock)
{
    public async Task<(BillingCustomerResult Customer, BillingSubscriptionResult Subscription)> SubscribeAsync(
        SubscriptionPlan plan,
        string currency,
        CancellationToken cancellationToken)
    {
        var organization = await organizations.FindByIdAsync(tenantContext.RequiredTenantId, cancellationToken)
            ?? throw new KeyNotFoundException("Organization was not found.");
        var customer = await provider.CreateCustomerAsync(organization.Id, organization.Name, cancellationToken);
        var subscription = await provider.CreateSubscriptionAsync(organization.Id, plan, currency, cancellationToken);
        organization.ChangePlan(plan);
        await organizations.SaveAsync(cancellationToken);
        return (customer, subscription);
    }

    public Task<ProrationPreview> PreviewAsync(
        SubscriptionPlan current,
        SubscriptionPlan target,
        string currency,
        CancellationToken cancellationToken) =>
        provider.PreviewPlanChangeAsync(current, target, currency, clock.UtcNow, cancellationToken);

    public async Task<ProrationPreview> ChangePlanAsync(
        string providerSubscriptionId,
        SubscriptionPlan target,
        string currency,
        CancellationToken cancellationToken)
    {
        var organization = await organizations.FindByIdAsync(tenantContext.RequiredTenantId, cancellationToken)
            ?? throw new KeyNotFoundException("Organization was not found.");
        var preview = await provider.PreviewPlanChangeAsync(
            organization.Plan, target, currency, clock.UtcNow, cancellationToken);
        await provider.ChangePlanAsync(providerSubscriptionId, target, cancellationToken);
        organization.ChangePlan(target);
        await organizations.SaveAsync(cancellationToken);
        return preview;
    }
}
