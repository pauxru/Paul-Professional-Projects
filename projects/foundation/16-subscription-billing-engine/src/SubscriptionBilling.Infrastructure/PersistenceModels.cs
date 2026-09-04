using SubscriptionBilling.Application;
using SubscriptionBilling.Domain;

namespace SubscriptionBilling.Infrastructure;

public sealed class ProductEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PlanEntity
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public string Name { get; set; } = string.Empty;
    public BillingIntervalUnit IntervalUnit { get; set; }
    public int IntervalCount { get; set; }
    public Guid? MeterId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PlanVersionEntity
{
    public Guid Id { get; set; }
    public Guid PlanId { get; set; }
    public int Version { get; set; }
    public DateTimeOffset EffectiveFrom { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string PricingJson { get; set; } = string.Empty;
    public bool TaxInclusive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class MeterEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public UsageAggregationMode Aggregation { get; set; }
    public decimal RoundingIncrement { get; set; }
    public UsageRoundingMode RoundingMode { get; set; }
}

public sealed class CustomerEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public string CountryCode { get; set; } = string.Empty;
    public bool TaxExempt { get; set; }
    public bool ReverseCharge { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class SubscriptionEntity
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public Guid PlanVersionId { get; set; }
    public long Quantity { get; set; }
    public SubscriptionState State { get; set; }
    public DateTimeOffset CurrentPeriodStart { get; set; }
    public DateTimeOffset CurrentPeriodEnd { get; set; }
    public DateTimeOffset AnchorOrigin { get; set; }
    public int AnchorDay { get; set; }
    public bool AnchorIsMonthEnd { get; set; }
    public DateTimeOffset? TrialEnd { get; set; }
    public TrialEndBehavior TrialEndBehavior { get; set; }
    public bool CancelAtPeriodEnd { get; set; }
    public bool IsAccessSuspended { get; set; }
    public SubscriptionState? StateBeforePause { get; set; }
    public Guid? CouponId { get; set; }
    public int CouponApplications { get; set; }
    public bool PreviouslyActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public int Version { get; set; }
}

public sealed class SubscriptionChangeEntity
{
    public Guid Id { get; set; }
    public Guid SubscriptionId { get; set; }
    public Guid OldPlanVersionId { get; set; }
    public Guid NewPlanVersionId { get; set; }
    public long OldQuantity { get; set; }
    public long NewQuantity { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
    public long NetAmountMinor { get; set; }
    public string Currency { get; set; } = string.Empty;
    public ProrationBehavior Behavior { get; set; }
}

public sealed class PendingChargeEntity
{
    public Guid Id { get; set; }
    public Guid SubscriptionId { get; set; }
    public InvoiceLineType Type { get; set; }
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset PeriodStart { get; set; }
    public DateTimeOffset PeriodEnd { get; set; }
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = string.Empty;
    public bool Taxable { get; set; }
    public bool Invoiced { get; set; }
    public Guid? InvoiceId { get; set; }
}

public sealed class UsageEventEntity
{
    public string EventId { get; set; } = string.Empty;
    public Guid SubscriptionId { get; set; }
    public Guid MeterId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public decimal Quantity { get; set; }
    public string? UniqueKey { get; set; }
    public string? AdjustmentOfEventId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public DateTimeOffset RollupPeriodStart { get; set; }
    public DateTimeOffset RollupPeriodEnd { get; set; }
    public LateUsageDisposition Disposition { get; set; }
}

public sealed class UsageRollupEntity
{
    public Guid Id { get; set; }
    public Guid SubscriptionId { get; set; }
    public Guid MeterId { get; set; }
    public DateTimeOffset PeriodStart { get; set; }
    public DateTimeOffset PeriodEnd { get; set; }
    public decimal AggregateValue { get; set; }
    public decimal SumValue { get; set; }
    public decimal MaxValue { get; set; }
    public decimal LastValue { get; set; }
    public DateTimeOffset? LastOccurredAt { get; set; }
    public string? LastEventId { get; set; }
    public long UniqueCount { get; set; }
    public long EventCount { get; set; }
    public long BillableUnits { get; set; }
    public bool Closed { get; set; }
}

public sealed class UsageUniqueKeyEntity
{
    public Guid Id { get; set; }
    public Guid SubscriptionId { get; set; }
    public Guid MeterId { get; set; }
    public DateTimeOffset PeriodStart { get; set; }
    public string UniqueKey { get; set; } = string.Empty;
}

public sealed class InvoiceEntity
{
    public Guid Id { get; set; }
    public string? Number { get; set; }
    public Guid CustomerId { get; set; }
    public Guid SubscriptionId { get; set; }
    public InvoiceStatus Status { get; set; }
    public DateTimeOffset PeriodStart { get; set; }
    public DateTimeOffset PeriodEnd { get; set; }
    public long SubtotalMinor { get; set; }
    public long DiscountMinor { get; set; }
    public long TaxMinor { get; set; }
    public long CreditAppliedMinor { get; set; }
    public long TotalMinor { get; set; }
    public string Currency { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? FinalizedAt { get; set; }
    public string BillingReason { get; set; } = "periodic";
}

public sealed class InvoiceLineEntity
{
    public Guid Id { get; set; }
    public Guid InvoiceId { get; set; }
    public InvoiceLineType Type { get; set; }
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset PeriodStart { get; set; }
    public DateTimeOffset PeriodEnd { get; set; }
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = string.Empty;
    public bool Taxable { get; set; }
    public bool RevenueRecognizedOverPeriod { get; set; }
}

public sealed class CouponEntity
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public CouponType Type { get; set; }
    public decimal Percentage { get; set; }
    public long? FixedAmountMinor { get; set; }
    public string? Currency { get; set; }
    public CouponDuration Duration { get; set; }
    public int? DurationCycles { get; set; }
    public int? MaxRedemptions { get; set; }
    public int RedemptionCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class CouponRedemptionEntity
{
    public Guid Id { get; set; }
    public Guid CouponId { get; set; }
    public Guid SubscriptionId { get; set; }
    public Guid InvoiceId { get; set; }
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = string.Empty;
    public DateTimeOffset RedeemedAt { get; set; }
}

public sealed class CreditEntity
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public long OriginalMinor { get; set; }
    public long RemainingMinor { get; set; }
    public string Currency { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed class CreditApplicationEntity
{
    public Guid Id { get; set; }
    public Guid CreditId { get; set; }
    public Guid InvoiceId { get; set; }
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = string.Empty;
    public DateTimeOffset AppliedAt { get; set; }
}

public sealed class CreditNoteEntity
{
    public Guid Id { get; set; }
    public Guid InvoiceId { get; set; }
    public string Number { get; set; } = string.Empty;
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PaymentAttemptEntity
{
    public Guid Id { get; set; }
    public Guid InvoiceId { get; set; }
    public int AttemptNumber { get; set; }
    public PaymentOutcome Outcome { get; set; }
    public string ProviderReference { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset AttemptedAt { get; set; }
}

public sealed class DunningCaseEntity
{
    public Guid Id { get; set; }
    public Guid InvoiceId { get; set; }
    public Guid SubscriptionId { get; set; }
    public DateTimeOffset InitialFailureAt { get; set; }
    public int AttemptsCompleted { get; set; }
    public bool Recovered { get; set; }
    public bool Escalated { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public string PaymentMethodToken { get; set; } = string.Empty;
}

public sealed class WebhookNonceEntity
{
    public string Nonce { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class OutboundWebhookEntity
{
    public Guid Id { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public int MaxAttempts { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public OutboundWebhookStatus Status { get; set; }
    public string? LastError { get; set; }
}

public sealed class IdempotencyRecordEntity
{
    public Guid Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Route { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public int ResponseStatusCode { get; set; }
    public string ResponseContentType { get; set; } = "application/json";
    public string ResponseBody { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class AuditRecordEntity
{
    public Guid Id { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string StateHash { get; set; } = string.Empty;
}

public sealed class SequenceEntity
{
    public string Name { get; set; } = string.Empty;
    public long NextValue { get; set; }
}
