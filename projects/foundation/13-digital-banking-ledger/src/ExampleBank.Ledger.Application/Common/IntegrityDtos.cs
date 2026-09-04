namespace ExampleBank.Ledger.Application.Common;

public sealed record ChainVerificationResult(
    bool IsValid,
    int EntriesChecked,
    long? FirstBrokenSequence,
    string? Detail);

public sealed record AccountReconciliation(
    Guid AccountId,
    string Code,
    long CachedDebitsMinor,
    long CachedCreditsMinor,
    long DerivedDebitsMinor,
    long DerivedCreditsMinor,
    bool IsReconciled);

public sealed record ReconciliationResult(
    bool IsReconciled,
    int AccountsChecked,
    IReadOnlyList<AccountReconciliation> Discrepancies);

/// <summary>Combined integrity report: hash-chain verification plus cached/derived balance reconciliation.</summary>
public sealed record IntegrityReport(
    ChainVerificationResult Chain,
    ReconciliationResult Reconciliation)
{
    public bool IsHealthy => Chain.IsValid && Reconciliation.IsReconciled;
}
