using ReconEngine.Domain.Entities;
using ReconEngine.Domain.Enums;

namespace ReconEngine.Application.Matching;

/// <summary>
/// A proposed match before it is persisted. It carries the records involved on each side, the rule
/// that produced it, a confidence score and the list of predicates that fired (the audit-grade
/// explanation). One-to-one, many-to-one, one-to-many, fee-adjusted and refund matches are all
/// expressed with the same shape.
/// </summary>
public sealed class MatchCandidate
{
    public required string RuleId { get; init; }
    public required MatchKind Kind { get; init; }
    public required decimal Confidence { get; init; }
    public required string Currency { get; init; }
    public required IReadOnlyList<ReconRecord> Internals { get; init; }
    public required IReadOnlyList<ReconRecord> Externals { get; init; }
    public long? ExpectedFeeMinor { get; init; }
    public long? FeeVarianceMinor { get; init; }
    public IReadOnlyList<string> Predicates { get; init; } = Array.Empty<string>();

    public long InternalAmountMinor => Internals.Sum(r => r.AmountMinor);
    public long ExternalAmountMinor => Externals.Sum(r => r.AmountMinor);
    public string Explanation => string.Join("; ", Predicates);

    public IEnumerable<ReconRecord> AllRecords => Internals.Concat(Externals);
    public IEnumerable<Guid> AllRecordIds => AllRecords.Select(r => r.Id);
}
