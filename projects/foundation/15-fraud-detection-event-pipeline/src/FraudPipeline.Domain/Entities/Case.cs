namespace FraudPipeline.Domain.Entities;

public enum CaseStatus
{
    New = 0,
    Assigned = 1,
    UnderInvestigation = 2,
    AwaitingApproval = 3,
    Disposed = 4
}

public enum CaseDisposition
{
    Unresolved = 0,
    ConfirmedFraud = 1,
    FalsePositive = 2,
    Inconclusive = 3
}

public sealed class CaseNote
{
    public Guid Id { get; private set; }
    public Guid CaseId { get; private set; }
    public string Author { get; private set; }
    public string Text { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private CaseNote() { Author = Text = ""; }

    public CaseNote(Guid id, Guid caseId, string author, string text, DateTimeOffset createdAt)
    {
        if (string.IsNullOrWhiteSpace(author)) throw new ArgumentException(nameof(author));
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException(nameof(text));
        Id = id;
        CaseId = caseId;
        Author = author;
        Text = text;
        CreatedAt = createdAt;
    }
}

/// <summary>
/// A fraud investigation case. Aggregates alerts by entity linkage
/// (same card/device/ip) and enforces a state machine + four-eyes on
/// large-exposure write-offs.
/// </summary>
public sealed class Case
{
    public Guid Id { get; private set; }
    public string PrimaryEntityKey { get; private set; }
    public decimal ExposureAmount { get; private set; }
    public string ExposureCurrency { get; private set; }
    public int PriorityScore { get; private set; }
    public CaseStatus Status { get; private set; }
    public CaseDisposition Disposition { get; private set; }
    public string? AssignedTo { get; private set; }
    public string? DispositionReason { get; private set; }
    public string? DispositionBy { get; private set; }
    public string? ApprovedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? DisposedAt { get; private set; }
    public DateTimeOffset LastActivityAt { get; private set; }

    private readonly List<CaseNote> _notes = new();
    public IReadOnlyList<CaseNote> Notes => _notes;

    private readonly List<Guid> _alertIds = new();
    public IReadOnlyList<Guid> AlertIds => _alertIds;

    /// <summary>
    /// Exposure threshold above which a second approver is required
    /// to record ConfirmedFraud (four-eyes).
    /// </summary>
    public const decimal FourEyesExposureThreshold = 10_000m;

    private Case() { PrimaryEntityKey = ExposureCurrency = ""; }

    public Case(Guid id, string primaryEntityKey, string currency, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty) throw new ArgumentException(nameof(id));
        if (string.IsNullOrWhiteSpace(primaryEntityKey)) throw new ArgumentException(nameof(primaryEntityKey));
        Id = id;
        PrimaryEntityKey = primaryEntityKey;
        ExposureCurrency = currency;
        CreatedAt = createdAt;
        LastActivityAt = createdAt;
        Status = CaseStatus.New;
        Disposition = CaseDisposition.Unresolved;
    }

    public void LinkAlert(Guid alertId, int score, decimal amount, DateTimeOffset at)
    {
        if (_alertIds.Contains(alertId)) return;
        _alertIds.Add(alertId);
        ExposureAmount += amount;
        if (score > PriorityScore) PriorityScore = score;
        LastActivityAt = at;
    }

    public void Assign(string investigator, DateTimeOffset at)
    {
        if (Status == CaseStatus.Disposed) throw new InvalidOperationException("Case already disposed.");
        if (string.IsNullOrWhiteSpace(investigator)) throw new ArgumentException(nameof(investigator));
        AssignedTo = investigator;
        Status = CaseStatus.Assigned;
        LastActivityAt = at;
    }

    public void StartInvestigation(DateTimeOffset at)
    {
        if (Status != CaseStatus.Assigned) throw new InvalidOperationException("Case must be Assigned to start investigation.");
        Status = CaseStatus.UnderInvestigation;
        LastActivityAt = at;
    }

    public void AddNote(Guid noteId, string author, string text, DateTimeOffset at)
    {
        if (Status == CaseStatus.Disposed) throw new InvalidOperationException("Case is disposed.");
        _notes.Add(new CaseNote(noteId, Id, author, text, at));
        LastActivityAt = at;
    }

    /// <summary>
    /// Propose a disposition. If exposure exceeds the four-eyes threshold and the
    /// disposition is ConfirmedFraud, the case transitions to AwaitingApproval
    /// and cannot be finalised until <see cref="Approve"/> is called by a second party.
    /// Otherwise it moves straight to Disposed.
    /// </summary>
    public void ProposeDisposition(CaseDisposition disposition, string reason, string by, DateTimeOffset at)
    {
        if (Status == CaseStatus.Disposed) throw new InvalidOperationException("Case is already disposed.");
        if (Status == CaseStatus.New) throw new InvalidOperationException("Case must be assigned before disposition.");
        if (disposition == CaseDisposition.Unresolved) throw new ArgumentException("Cannot dispose as Unresolved.", nameof(disposition));
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException(nameof(reason));
        if (string.IsNullOrWhiteSpace(by)) throw new ArgumentException(nameof(by));

        Disposition = disposition;
        DispositionReason = reason;
        DispositionBy = by;
        LastActivityAt = at;

        var needsFourEyes = disposition == CaseDisposition.ConfirmedFraud && ExposureAmount >= FourEyesExposureThreshold;
        if (needsFourEyes)
        {
            Status = CaseStatus.AwaitingApproval;
        }
        else
        {
            Status = CaseStatus.Disposed;
            DisposedAt = at;
        }
    }

    public void Approve(string approver, DateTimeOffset at)
    {
        if (Status != CaseStatus.AwaitingApproval) throw new InvalidOperationException("Case is not awaiting approval.");
        if (string.IsNullOrWhiteSpace(approver)) throw new ArgumentException(nameof(approver));
        if (string.Equals(approver, DispositionBy, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Approver must be a different party from the proposer (four-eyes).");
        ApprovedBy = approver;
        Status = CaseStatus.Disposed;
        DisposedAt = at;
        LastActivityAt = at;
    }
}
