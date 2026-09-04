using System.Text.Json;
using ReconEngine.Domain.Abstractions;
using ReconEngine.Domain.Enums;

namespace ReconEngine.Domain.Entities;

/// <summary>
/// A reconciliation discrepancy and its manual-resolution workflow. The lifecycle is a strict state
/// machine — Open → Assigned → (Resolved | PendingApproval → Resolved) and Resolved → Reopened —
/// with a four-eyes gate on write-offs above a configurable threshold. Every transition appends an
/// immutable audit entry; audit records are never mutated or removed by application code.
/// </summary>
public class ReconciliationException
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Stable, content-derived identity so the same discrepancy keeps one identity across runs.</summary>
    public string ExceptionKey { get; set; } = string.Empty;

    public Guid CreatedByRunId { get; set; }

    public ExceptionType Type { get; set; }

    public ExceptionSeverity Severity { get; set; }

    public string SuggestedAction { get; set; } = string.Empty;

    public string Currency { get; set; } = string.Empty;

    /// <summary>Signed monetary magnitude of the discrepancy (minor units), used for aging and the write-off gate.</summary>
    public long AmountMinor { get; set; }

    /// <summary>JSON array of the record ids involved in this exception.</summary>
    public string RecordIdsJson { get; set; } = "[]";

    public ExceptionStatus Status { get; private set; } = ExceptionStatus.Open;

    public string? AssignedTo { get; private set; }

    public ResolutionReasonCode? ResolutionReasonCode { get; private set; }

    public string? ResolvedBy { get; private set; }

    public DateTime? ResolvedAtUtc { get; private set; }

    public bool ApprovalRequired { get; private set; }

    public string? ApprovedBy { get; private set; }

    public DateTime? ApprovedAtUtc { get; private set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>Optimistic-concurrency token.</summary>
    public int Version { get; set; }

    public ICollection<ExceptionComment> Comments { get; set; } = new List<ExceptionComment>();

    public ICollection<ExceptionAuditEntry> AuditTrail { get; set; } = new List<ExceptionAuditEntry>();

    public bool IsResolved => Status == ExceptionStatus.Resolved;

    public bool IsOpen => Status is ExceptionStatus.Open or ExceptionStatus.Assigned
        or ExceptionStatus.PendingApproval or ExceptionStatus.Reopened;

    public IReadOnlyList<Guid> GetRecordIds() =>
        JsonSerializer.Deserialize<List<Guid>>(RecordIdsJson) ?? new List<Guid>();

    public void SetRecordIds(IEnumerable<Guid> ids) =>
        RecordIdsJson = JsonSerializer.Serialize(ids.Distinct().ToList());

    // ---------------------------------------------------------------- workflow transitions ----

    public void Assign(string assignee, string actor, IClock clock)
    {
        if (string.IsNullOrWhiteSpace(assignee))
            throw new DomainException("Assignee is required.");
        RequireStatus("assign", ExceptionStatus.Open, ExceptionStatus.Assigned, ExceptionStatus.Reopened);

        AssignedTo = assignee;
        Transition(ExceptionStatus.Assigned, actor, clock, $"Assigned to {assignee}");
    }

    public void AddComment(string author, string text, IClock clock)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new DomainException("Comment text is required.");

        var now = clock.UtcNow;
        Comments.Add(new ExceptionComment
        {
            ExceptionId = Id,
            Author = string.IsNullOrWhiteSpace(author) ? "unknown" : author,
            Text = text,
            CreatedAtUtc = now,
        });
        UpdatedAtUtc = now;
        Audit(author, "Commented", Status, Status, clock, text);
    }

    /// <summary>
    /// Resolve the exception. A <see cref="Enums.ResolutionReasonCode.WriteOff"/> whose magnitude is at
    /// or above <paramref name="writeOffApprovalThresholdMinor"/> does not resolve immediately: it moves
    /// to <see cref="ExceptionStatus.PendingApproval"/> and must be approved by a different user.
    /// </summary>
    public void Resolve(
        ResolutionReasonCode reason,
        string actor,
        string? note,
        long writeOffApprovalThresholdMinor,
        IClock clock)
    {
        if (string.IsNullOrWhiteSpace(actor))
            throw new DomainException("An actor is required to resolve an exception.");
        RequireStatus("resolve", ExceptionStatus.Open, ExceptionStatus.Assigned, ExceptionStatus.Reopened);

        ResolutionReasonCode = reason;

        var needsApproval = reason == Enums.ResolutionReasonCode.WriteOff
                            && Math.Abs(AmountMinor) >= writeOffApprovalThresholdMinor;

        if (needsApproval)
        {
            ApprovalRequired = true;
            ResolvedBy = actor; // proposer; the resolution only takes effect on approval
            Transition(ExceptionStatus.PendingApproval, actor, clock,
                note ?? $"Write-off proposed by {actor}; awaiting four-eyes approval");
            return;
        }

        ApprovalRequired = false;
        ResolvedBy = actor;
        ResolvedAtUtc = clock.UtcNow;
        Transition(ExceptionStatus.Resolved, actor, clock, note ?? $"Resolved as {reason}");
    }

    public void Approve(string approver, IClock clock)
    {
        if (string.IsNullOrWhiteSpace(approver))
            throw new DomainException("An approver is required.");
        RequireStatus("approve", ExceptionStatus.PendingApproval);

        if (string.Equals(approver, ResolvedBy, StringComparison.OrdinalIgnoreCase))
            throw new InvalidStateTransitionException(
                "Four-eyes violation: a write-off must be approved by a different user than the one who proposed it.");

        ApprovedBy = approver;
        ApprovedAtUtc = clock.UtcNow;
        ResolvedAtUtc = clock.UtcNow;
        Transition(ExceptionStatus.Resolved, approver, clock, $"Write-off approved by {approver}");
    }

    public void RejectApproval(string approver, string? note, IClock clock)
    {
        if (string.IsNullOrWhiteSpace(approver))
            throw new DomainException("An approver is required.");
        RequireStatus("reject", ExceptionStatus.PendingApproval);

        if (string.Equals(approver, ResolvedBy, StringComparison.OrdinalIgnoreCase))
            throw new InvalidStateTransitionException(
                "Four-eyes violation: the proposer cannot reject their own write-off.");

        ApprovalRequired = false;
        ResolutionReasonCode = null;
        ResolvedBy = null;
        var target = AssignedTo is null ? ExceptionStatus.Open : ExceptionStatus.Assigned;
        Transition(target, approver, clock, note ?? $"Write-off rejected by {approver}");
    }

    public void Reopen(string actor, string? note, IClock clock)
    {
        RequireStatus("reopen", ExceptionStatus.Resolved);

        ResolutionReasonCode = null;
        ResolvedBy = null;
        ResolvedAtUtc = null;
        ApprovalRequired = false;
        ApprovedBy = null;
        ApprovedAtUtc = null;
        Transition(ExceptionStatus.Reopened, actor, clock, note ?? "Reopened");
    }

    // ------------------------------------------------------------------------------ helpers ----

    private void RequireStatus(string operation, params ExceptionStatus[] allowed)
    {
        if (!allowed.Contains(Status))
            throw new InvalidStateTransitionException(
                $"Cannot {operation} an exception in state {Status}. Allowed states: {string.Join(", ", allowed)}.");
    }

    private void Transition(ExceptionStatus to, string actor, IClock clock, string detail)
    {
        var from = Status;
        Status = to;
        UpdatedAtUtc = clock.UtcNow;
        Version++;
        Audit(actor, $"{from}->{to}", from, to, clock, detail);
    }

    private void Audit(string actor, string action, ExceptionStatus from, ExceptionStatus to, IClock clock, string? detail)
    {
        AuditTrail.Add(new ExceptionAuditEntry
        {
            ExceptionId = Id,
            Actor = string.IsNullOrWhiteSpace(actor) ? "system" : actor,
            Action = action,
            FromStatus = from,
            ToStatus = to,
            Detail = detail,
            AtUtc = clock.UtcNow,
        });
    }
}

/// <summary>A free-text comment attached to an exception during triage.</summary>
public class ExceptionComment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ExceptionId { get; set; }
    public string Author { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>An append-only audit record of a single workflow action on an exception.</summary>
public class ExceptionAuditEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ExceptionId { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public ExceptionStatus FromStatus { get; set; }
    public ExceptionStatus ToStatus { get; set; }
    public string? Detail { get; set; }
    public DateTime AtUtc { get; set; }
}
