using ReconEngine.Domain.Enums;

namespace ReconEngine.Domain.Entities;

/// <summary>
/// A confirmed match produced by the engine. A match links one-or-more internal records to
/// one-or-more external records (via <see cref="Entries"/>), and records exactly which rule and
/// ruleset version produced it, its confidence, and a human-readable explanation of the predicates
/// that fired — everything an auditor needs to understand why two lines were considered the same.
/// </summary>
public class Match
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RunId { get; set; }

    public string RuleId { get; set; } = string.Empty;

    public string RuleSetVersionTag { get; set; } = string.Empty;

    public MatchKind MatchType { get; set; }

    /// <summary>Confidence in [0,1]. Exact rules score 1.0; fuzzy rules score lower.</summary>
    public decimal Confidence { get; set; }

    /// <summary>Human-readable list of the predicates that fired, for audit and the UI.</summary>
    public string Explanation { get; set; } = string.Empty;

    public string Currency { get; set; } = string.Empty;

    public long InternalAmountMinor { get; set; }

    public long ExternalAmountMinor { get; set; }

    public long? ExpectedFeeMinor { get; set; }

    public long? FeeVarianceMinor { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public ICollection<MatchEntry> Entries { get; set; } = new List<MatchEntry>();
}

/// <summary>Join between a <see cref="Match"/> and one of the records it links.</summary>
public class MatchEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MatchId { get; set; }
    public Guid RecordId { get; set; }
    public RecordSource Side { get; set; }
}
