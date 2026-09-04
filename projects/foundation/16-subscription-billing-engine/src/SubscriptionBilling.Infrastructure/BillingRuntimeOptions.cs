using SubscriptionBilling.Domain;

namespace SubscriptionBilling.Infrastructure;

public sealed record BillingRuntimeOptions
{
    public ClosedPeriodUsageBehavior ClosedPeriodUsageBehavior { get; init; } =
        ClosedPeriodUsageBehavior.Reject;

    public TaxRoundingLevel TaxRoundingLevel { get; init; } = TaxRoundingLevel.Line;

    public decimal UsTaxRate { get; init; }

    public IReadOnlyList<int> DunningRetryDays { get; init; } = [1, 3, 5, 7];

    public IReadOnlyList<string> OutboundWebhookEndpoints { get; init; } = [];

    public string WebhookSigningSecret { get; init; } =
        "dev-only-not-a-real-secret-webhook-key-0123456789";
}
