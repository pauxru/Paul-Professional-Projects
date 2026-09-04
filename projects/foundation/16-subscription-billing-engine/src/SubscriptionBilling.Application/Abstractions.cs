using SubscriptionBilling.Domain;

namespace SubscriptionBilling.Application;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface IIdGenerator
{
    Guid NewGuid();
}

public sealed record TaxRequest(
    string CountryCode,
    IReadOnlyList<TaxLine> Lines,
    TaxPricingMode PricingMode,
    TaxRoundingLevel RoundingLevel,
    bool Exempt,
    bool ReverseCharge);

public interface ITaxProvider
{
    TaxCalculation Calculate(TaxRequest request);
}

public enum PaymentOutcome
{
    Succeeded,
    InsufficientFunds,
    ExpiredCard,
    HardDecline,
    GatewayTimeout
}

public sealed record PaymentRequest(
    Guid InvoiceId,
    Money Amount,
    string PaymentMethodToken,
    string IdempotencyKey);

public sealed record PaymentProviderResult(
    PaymentOutcome Outcome,
    string ProviderReference,
    string Message)
{
    public bool Succeeded => Outcome == PaymentOutcome.Succeeded;
    public bool Transient => Outcome is PaymentOutcome.InsufficientFunds or PaymentOutcome.GatewayTimeout;
}

public interface IPaymentProvider
{
    Task<PaymentProviderResult> ChargeAsync(
        PaymentRequest request,
        CancellationToken cancellationToken);
}

public interface IDunningNotificationHook
{
    Task NotifyAttemptAsync(
        Guid invoiceId,
        int attemptNumber,
        PaymentOutcome outcome,
        CancellationToken cancellationToken);

    Task NotifyEscalationAsync(
        Guid invoiceId,
        CancellationToken cancellationToken);
}

public interface IWebhookReplayStore
{
    Task<bool> TryRecordAsync(
        string nonce,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken);
}

public interface IOutboundWebhookTransport
{
    Task<bool> SendAsync(
        Uri endpoint,
        string eventType,
        string payload,
        string signature,
        CancellationToken cancellationToken);
}

public interface ITokenIssuer
{
    string Issue(string subject, IEnumerable<string> scopes, TimeSpan lifetime);
}

public sealed record Page<T>(
    IReadOnlyList<T> Items,
    int PageNumber,
    int PageSize,
    long TotalCount,
    int TotalPages);

public sealed record ProductView(Guid Id, string Name, string Description, bool IsActive);

public sealed record PlanVersionView(
    Guid Id,
    int Version,
    DateTimeOffset EffectiveFrom,
    string Currency,
    PricingConfiguration Pricing,
    bool TaxInclusive);

public sealed record PlanView(
    Guid Id,
    Guid ProductId,
    string Name,
    BillingIntervalUnit IntervalUnit,
    int IntervalCount,
    Guid? MeterId,
    IReadOnlyList<PlanVersionView> Versions);

public sealed record MeterView(
    Guid Id,
    string Name,
    string Unit,
    UsageAggregationMode Aggregation,
    decimal RoundingIncrement,
    UsageRoundingMode RoundingMode);

public sealed record CustomerView(
    Guid Id,
    string Name,
    string Currency,
    string CountryCode,
    bool TaxExempt,
    bool ReverseCharge,
    long CreditBalanceMinor);

public sealed record SubscriptionView(
    Guid Id,
    Guid CustomerId,
    Guid PlanVersionId,
    long Quantity,
    SubscriptionState State,
    DateTimeOffset CurrentPeriodStart,
    DateTimeOffset CurrentPeriodEnd,
    int AnchorDay,
    bool AnchorIsMonthEnd,
    DateTimeOffset? TrialEnd,
    bool CancelAtPeriodEnd,
    bool IsAccessSuspended);

public sealed record UsageReceipt(
    string EventId,
    LateUsageDisposition Disposition,
    bool Duplicate,
    decimal RollupValue,
    long BillableUnits);

public sealed record OneOffChargeView(
    Guid Id,
    Guid SubscriptionId,
    string Description,
    long AmountMinor,
    string Currency,
    bool Taxable,
    bool Invoiced,
    Guid? InvoiceId);

public sealed record InvoiceLineView(
    Guid Id,
    InvoiceLineType Type,
    string Description,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    long AmountMinor,
    string Currency,
    bool Taxable);

public sealed record InvoiceView(
    Guid Id,
    string? Number,
    Guid CustomerId,
    Guid SubscriptionId,
    InvoiceStatus Status,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    long SubtotalMinor,
    long DiscountMinor,
    long TaxMinor,
    long CreditAppliedMinor,
    long TotalMinor,
    string Currency,
    IReadOnlyList<InvoiceLineView> Lines);

public sealed record InvoicePreviewView(
    Guid SubscriptionId,
    Guid CurrentPlanVersionId,
    Guid ProposedPlanVersionId,
    DateTimeOffset AsOf,
    IReadOnlyList<InvoiceLineView> Lines,
    long NetAmountMinor,
    long DiscountMinor,
    long TaxMinor,
    long CreditAvailableMinor,
    long TotalMinor,
    string Currency,
    bool WouldInvoiceImmediately);

public sealed record CouponView(
    Guid Id,
    string Code,
    CouponType Type,
    decimal Percentage,
    long? FixedAmountMinor,
    string? Currency,
    CouponDuration Duration,
    int? DurationCycles,
    int? MaxRedemptions,
    int RedemptionCount);

public sealed record CreditView(
    Guid Id,
    Guid CustomerId,
    long OriginalMinor,
    long RemainingMinor,
    string Currency,
    DateTimeOffset CreatedAt,
    string Reason);

public sealed record CreditNoteView(
    Guid Id,
    Guid InvoiceId,
    string Number,
    long AmountMinor,
    string Currency,
    string Reason,
    DateTimeOffset CreatedAt);

public sealed record MrrReportView(
    string Currency,
    long OpeningMinor,
    long NewMinor,
    long ExpansionMinor,
    long ContractionMinor,
    long ChurnMinor,
    long ReactivationMinor,
    long ClosingMinor,
    long ArrMinor);

public sealed record InvoiceRunResult(
    int Generated,
    int AlreadyExisted,
    IReadOnlyList<Guid> InvoiceIds);

public sealed record CreateProductCommand(string Name, string Description);

public sealed record CreatePlanCommand(
    Guid ProductId,
    string Name,
    BillingIntervalUnit IntervalUnit,
    int IntervalCount,
    Guid? MeterId,
    DateTimeOffset EffectiveFrom,
    string Currency,
    PricingConfiguration Pricing,
    bool TaxInclusive);

public sealed record AddPlanVersionCommand(
    DateTimeOffset EffectiveFrom,
    string Currency,
    PricingConfiguration Pricing,
    bool TaxInclusive);

public sealed record CreateMeterCommand(
    string Name,
    string Unit,
    UsageAggregationMode Aggregation,
    decimal RoundingIncrement,
    UsageRoundingMode RoundingMode);

public sealed record CreateCustomerCommand(
    string Name,
    string Currency,
    string CountryCode,
    bool TaxExempt,
    bool ReverseCharge);

public sealed record CreateSubscriptionCommand(
    Guid CustomerId,
    Guid PlanVersionId,
    long Quantity,
    DateTimeOffset? StartAt,
    DateTimeOffset? TrialEnd,
    TrialEndBehavior TrialEndBehavior,
    Guid? CouponId);

public sealed record ChangeSubscriptionCommand(
    Guid PlanVersionId,
    long Quantity,
    DateTimeOffset? ChangeAt,
    ProrationBehavior ProrationBehavior);

public sealed record CancelSubscriptionCommand(bool Immediately);

public sealed record AddOneOffChargeCommand(
    string Description,
    long AmountMinor,
    string Currency,
    bool Taxable,
    bool InvoiceImmediately);

public sealed record RecordUsageCommand(
    string EventId,
    Guid SubscriptionId,
    Guid MeterId,
    DateTimeOffset OccurredAt,
    decimal Quantity,
    string? UniqueKey,
    string? AdjustmentOfEventId);

public sealed record PreviewInvoiceCommand(
    Guid SubscriptionId,
    Guid ProposedPlanVersionId,
    long ProposedQuantity,
    DateTimeOffset? AsOf,
    ProrationBehavior ProrationBehavior);

public sealed record CreateCouponCommand(
    string Code,
    CouponType Type,
    decimal Percentage,
    long? FixedAmountMinor,
    string? Currency,
    CouponDuration Duration,
    int? DurationCycles,
    int? MaxRedemptions);

public sealed record AddCreditCommand(
    Guid CustomerId,
    long AmountMinor,
    string Currency,
    string Reason);

public sealed record CreateCreditNoteCommand(long AmountMinor, string Reason);

public interface IBillingEngine
{
    Task<ProductView> CreateProductAsync(CreateProductCommand command, CancellationToken cancellationToken);
    Task<Page<ProductView>> ListProductsAsync(int page, int pageSize, CancellationToken cancellationToken);
    Task<PlanView> CreatePlanAsync(CreatePlanCommand command, CancellationToken cancellationToken);
    Task<PlanVersionView> AddPlanVersionAsync(Guid planId, AddPlanVersionCommand command, CancellationToken cancellationToken);
    Task<Page<PlanView>> ListPlansAsync(int page, int pageSize, CancellationToken cancellationToken);
    Task<MeterView> CreateMeterAsync(CreateMeterCommand command, CancellationToken cancellationToken);
    Task<Page<MeterView>> ListMetersAsync(int page, int pageSize, CancellationToken cancellationToken);
    Task<CustomerView> CreateCustomerAsync(CreateCustomerCommand command, CancellationToken cancellationToken);
    Task<Page<CustomerView>> ListCustomersAsync(int page, int pageSize, CancellationToken cancellationToken);
    Task<SubscriptionView> CreateSubscriptionAsync(CreateSubscriptionCommand command, CancellationToken cancellationToken);
    Task<Page<SubscriptionView>> ListSubscriptionsAsync(int page, int pageSize, CancellationToken cancellationToken);
    Task<SubscriptionView> ChangeSubscriptionAsync(Guid id, ChangeSubscriptionCommand command, CancellationToken cancellationToken);
    Task<SubscriptionView> PauseSubscriptionAsync(Guid id, CancellationToken cancellationToken);
    Task<SubscriptionView> ResumeSubscriptionAsync(Guid id, CancellationToken cancellationToken);
    Task<SubscriptionView> CancelSubscriptionAsync(Guid id, CancelSubscriptionCommand command, CancellationToken cancellationToken);
    Task<SubscriptionView> ReactivateSubscriptionAsync(Guid id, CancellationToken cancellationToken);
    Task<OneOffChargeView> AddOneOffChargeAsync(Guid id, AddOneOffChargeCommand command, CancellationToken cancellationToken);
    Task<UsageReceipt> RecordUsageAsync(RecordUsageCommand command, CancellationToken cancellationToken);
    Task<InvoicePreviewView> PreviewInvoiceAsync(PreviewInvoiceCommand command, CancellationToken cancellationToken);
    Task<InvoiceRunResult> RunInvoicesAsync(CancellationToken cancellationToken);
    Task<Page<InvoiceView>> ListInvoicesAsync(int page, int pageSize, CancellationToken cancellationToken);
    Task<InvoiceView?> GetInvoiceAsync(Guid id, CancellationToken cancellationToken);
    Task<InvoiceView> VoidInvoiceAsync(Guid id, CancellationToken cancellationToken);
    Task<InvoiceView> PayInvoiceAsync(Guid id, string paymentMethodToken, CancellationToken cancellationToken);
    Task<CreditNoteView> CreateCreditNoteAsync(Guid invoiceId, CreateCreditNoteCommand command, CancellationToken cancellationToken);
    Task<CouponView> CreateCouponAsync(CreateCouponCommand command, CancellationToken cancellationToken);
    Task<Page<CouponView>> ListCouponsAsync(int page, int pageSize, CancellationToken cancellationToken);
    Task<CreditView> AddCreditAsync(AddCreditCommand command, CancellationToken cancellationToken);
    Task<MrrReportView> GetMrrAsync(string currency, CancellationToken cancellationToken);
}

public interface IPaymentWebhookProcessor
{
    Task ProcessAsync(string eventType, Guid invoiceId, CancellationToken cancellationToken);
}
