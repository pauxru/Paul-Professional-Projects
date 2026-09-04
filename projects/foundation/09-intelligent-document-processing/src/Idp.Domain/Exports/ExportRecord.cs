namespace Idp.Domain.Exports;

public enum ExportStatus
{
    Pending = 0,
    Succeeded = 1,
    Failed = 2,
    DeadLettered = 3
}

/// <summary>
/// Tracks a downstream ERP export attempt with an idempotency key, bounded retries and a
/// dead-letter terminal state. The idempotency key prevents the simulated ERP from booking a
/// document twice if a retry succeeds after an ambiguous failure.
/// </summary>
public sealed class ExportRecord
{
    public Guid Id { get; private set; }
    public Guid DocumentId { get; private set; }
    public string IdempotencyKey { get; private set; } = default!;
    public ExportStatus Status { get; private set; }
    public string Format { get; private set; } = default!;
    public int Attempts { get; private set; }
    public int MaxAttempts { get; private set; }
    public string? LastError { get; private set; }
    public string? OutboxPath { get; private set; }
    public string? ErpReference { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    private ExportRecord() { }

    public ExportRecord(
        Guid documentId, string idempotencyKey, string format, int maxAttempts, DateTime nowUtc)
    {
        Id = Guid.NewGuid();
        DocumentId = documentId;
        IdempotencyKey = idempotencyKey;
        Format = format;
        Status = ExportStatus.Pending;
        MaxAttempts = maxAttempts;
        CreatedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    public bool CanAttempt => Status is ExportStatus.Pending or ExportStatus.Failed
        && Attempts < MaxAttempts;

    public void RecordAttempt(DateTime nowUtc)
    {
        Attempts += 1;
        UpdatedAtUtc = nowUtc;
    }

    public void MarkSucceeded(string? erpReference, string? outboxPath, DateTime nowUtc)
    {
        Status = ExportStatus.Succeeded;
        ErpReference = erpReference;
        OutboxPath = outboxPath;
        LastError = null;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Record a failed attempt; dead-letter once retries are exhausted.</summary>
    public void MarkAttemptFailed(string error, DateTime nowUtc)
    {
        LastError = error;
        UpdatedAtUtc = nowUtc;
        Status = Attempts >= MaxAttempts ? ExportStatus.DeadLettered : ExportStatus.Failed;
    }

    public void ForceDeadLetter(string error, DateTime nowUtc)
    {
        LastError = error;
        Status = ExportStatus.DeadLettered;
        UpdatedAtUtc = nowUtc;
    }

    public void ResetForReexport(DateTime nowUtc)
    {
        Status = ExportStatus.Pending;
        Attempts = 0;
        LastError = null;
        UpdatedAtUtc = nowUtc;
    }
}
