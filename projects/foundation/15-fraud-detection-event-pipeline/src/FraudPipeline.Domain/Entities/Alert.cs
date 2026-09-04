namespace FraudPipeline.Domain.Entities;

public enum AlertStatus
{
    Open = 0,
    Assigned = 1,
    UnderReview = 2,
    Closed = 3
}

/// <summary>
/// An alert raised for a transaction that scored above the alerting threshold.
/// Grouped into a Case via CaseId.
/// </summary>
public sealed class Alert
{
    public Guid Id { get; private set; }
    public Guid TransactionId { get; private set; }
    public Guid? CaseId { get; private set; }
    public string PrimaryEntityKey { get; private set; }
    public int Score { get; private set; }
    public string ReasonSummary { get; private set; }
    public AlertStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }

    private Alert() { PrimaryEntityKey = ReasonSummary = ""; }

    public Alert(Guid id, Guid transactionId, string primaryEntityKey, int score, string reasonSummary, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty) throw new ArgumentException("Id required.", nameof(id));
        if (transactionId == Guid.Empty) throw new ArgumentException("TransactionId required.", nameof(transactionId));
        if (string.IsNullOrWhiteSpace(primaryEntityKey)) throw new ArgumentException("PrimaryEntityKey required.", nameof(primaryEntityKey));
        if (score < 0 || score > 1000) throw new ArgumentOutOfRangeException(nameof(score));
        Id = id;
        TransactionId = transactionId;
        PrimaryEntityKey = primaryEntityKey;
        Score = score;
        ReasonSummary = reasonSummary;
        Status = AlertStatus.Open;
        CreatedAt = createdAt;
    }

    public void AttachToCase(Guid caseId)
    {
        if (caseId == Guid.Empty) throw new ArgumentException("CaseId required.", nameof(caseId));
        CaseId = caseId;
    }

    public void Close(DateTimeOffset at)
    {
        Status = AlertStatus.Closed;
        ClosedAt = at;
    }
}
