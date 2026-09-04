namespace Contoso.Payments.Application.Reconciliation;

public enum DiscrepancyKind
{
    Matched = 0,
    MissingInProvider = 1,
    MissingInternally = 2,
    AmountMismatch = 3,
    DuplicateInProvider = 4,
    StatusMismatch = 5
}

public sealed class ReconciliationRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset CompletedAtUtc { get; set; }
    public string SourceFileName { get; set; } = string.Empty;
    public int TotalProviderRows { get; set; }
    public int TotalInternalRows { get; set; }
    public int MatchedCount { get; set; }
    public int MissingInProviderCount { get; set; }
    public int MissingInternallyCount { get; set; }
    public int AmountMismatchCount { get; set; }
    public int DuplicateCount { get; set; }
    public int StatusMismatchCount { get; set; }
}

public sealed class ReconciliationDiscrepancy
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public DiscrepancyKind Kind { get; set; }
    public string? PaymentIntentId { get; set; }
    public string? ProviderReference { get; set; }
    public long? InternalMinorUnits { get; set; }
    public long? ProviderMinorUnits { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string? InternalStatus { get; set; }
    public string? ProviderStatus { get; set; }
    public string Notes { get; set; } = string.Empty;
}
