using System.ComponentModel.DataAnnotations.Schema;
using ReconEngine.Domain.Enums;
using ReconEngine.Domain.ValueObjects;

namespace ReconEngine.Domain.Entities;

/// <summary>
/// A normalised transaction (internal) or settlement line (external). Amounts are stored as minor
/// units plus an ISO currency code; the timestamp is normalised to UTC at ingestion. The
/// <see cref="RowHash"/> is a stable identity used for duplicate detection.
/// </summary>
public class ReconRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ImportBatchId { get; set; }

    public RecordSource Source { get; set; }

    /// <summary>Reference id exactly as it appeared in the source file.</summary>
    public string RawReference { get; set; } = string.Empty;

    /// <summary>Canonicalised reference used for matching (trim/case/prefix rules applied).</summary>
    public string CanonicalReference { get; set; } = string.Empty;

    /// <summary>A secondary reference used by composite rules (e.g. merchant order id).</summary>
    public string? CounterpartyReference { get; set; }

    public long AmountMinor { get; set; }

    public string Currency { get; set; } = string.Empty;

    /// <summary>Fee reported on this line, when the file carries one (external settlement lines usually do).</summary>
    public long? FeeMinor { get; set; }

    public DateTime TransactionDateUtc { get; set; }

    /// <summary>The value date (UTC day) used for windowing and duplicate hashing.</summary>
    public DateOnly ValueDate { get; set; }

    /// <summary>Fixed UTC offset of the source system, retained for auditability (e.g. "+03:00").</summary>
    public string SourceTimeZone { get; set; } = "+00:00";

    public TransactionStatus Status { get; set; }

    public string RowHash { get; set; } = string.Empty;

    public int LineNumber { get; set; }

    public DateTime IngestedAtUtc { get; set; }

    // ---- reconciliation state (mutated by runs / manual resolution) ----

    public ReconStatus ReconStatus { get; set; } = ReconStatus.Pending;

    public Guid? LastRunId { get; set; }

    [NotMapped]
    public Money Amount => new(AmountMinor, Currency);

    [NotMapped]
    public Money? Fee => FeeMinor is null ? null : new Money(FeeMinor.Value, Currency);
}
