using ReconEngine.Domain.Enums;

namespace ReconEngine.Domain.Entities;

/// <summary>
/// An immutable snapshot of a single reconciliation run: which ruleset ran, over what window,
/// the input checksum, the counts and monetary totals, whether the balance assertion held, and
/// how long it took. Runs are never mutated after completion; re-running the same inputs creates a
/// new run whose figures are identical.
/// </summary>
public class ReconciliationRun
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RuleSetId { get; set; }

    public string RuleSetVersionTag { get; set; } = string.Empty;

    public RunStatus Status { get; set; } = RunStatus.Pending;

    /// <summary>Inclusive lower/upper bounds of the value-date window, when the run is incremental.</summary>
    public DateOnly? WindowFrom { get; set; }
    public DateOnly? WindowTo { get; set; }

    /// <summary>Deterministic checksum of the working-set row hashes — equal inputs ⇒ equal checksum.</summary>
    public string InputChecksum { get; set; } = string.Empty;

    public int InternalRecordCount { get; set; }
    public int ExternalRecordCount { get; set; }
    public int MatchCount { get; set; }
    public int MatchedInternalCount { get; set; }
    public int MatchedExternalCount { get; set; }
    public int CarriedForwardCount { get; set; }
    public int ExceptionCount { get; set; }

    /// <summary>Per-currency monetary totals (internal / matched / unmatched), serialized as JSON.</summary>
    public string TotalsJson { get; set; } = "{}";

    /// <summary>Counts by <see cref="ExceptionType"/>, serialized as JSON.</summary>
    public string ExceptionBreakdownJson { get; set; } = "{}";

    public bool BalanceAssertionPassed { get; set; }

    public string? BalanceAssertionDetail { get; set; }

    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public long DurationMs { get; set; }

    public string TriggeredBy { get; set; } = "system";

    public string? Notes { get; set; }
}
