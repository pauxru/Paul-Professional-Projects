using AgentPlatform.Domain.Tools;

namespace AgentPlatform.Domain.Approvals;

/// <summary>Status of a human-in-the-loop approval task.</summary>
public enum ApprovalStatus
{
    Pending,
    Approved,
    Rejected,
    Expired,
}

/// <summary>
/// A pending decision for a mutating/external action a run wants to take. It captures the full
/// proposed action, its arguments and the reasoning trace so a human can make an informed call.
/// Decisions are audited (who/when/notes) and require the <c>agents:approve</c> permission.
/// </summary>
public sealed class ApprovalTask
{
    private ApprovalTask() { } // EF

    public ApprovalTask(string id, string runId, string stepId, string tenantId, string title,
        string proposedActionJson, string reasoningTrace, ToolRiskLevel riskLevel,
        DateTimeOffset requestedAt, DateTimeOffset expiresAt)
    {
        Id = id;
        RunId = runId;
        StepId = stepId;
        TenantId = tenantId;
        Title = title;
        ProposedActionJson = proposedActionJson;
        ReasoningTrace = reasoningTrace;
        RiskLevel = riskLevel;
        RequestedAt = requestedAt;
        ExpiresAt = expiresAt;
        Status = ApprovalStatus.Pending;
    }

    public string Id { get; private set; } = string.Empty;
    public string RunId { get; private set; } = string.Empty;
    public string StepId { get; private set; } = string.Empty;
    public string TenantId { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public string ProposedActionJson { get; private set; } = "{}";
    public string ReasoningTrace { get; private set; } = string.Empty;
    public ToolRiskLevel RiskLevel { get; private set; }

    public ApprovalStatus Status { get; private set; }
    public bool WasModified { get; private set; }
    public string? ModifiedArgumentsJson { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }
    public string? DecidedBy { get; private set; }
    public string? DecisionNotes { get; private set; }

    public bool IsPending => Status == ApprovalStatus.Pending;

    public void Approve(string decidedBy, string? notes, DateTimeOffset now)
    {
        EnsurePending();
        Status = ApprovalStatus.Approved;
        Decide(decidedBy, notes, now);
    }

    public void ApproveWithModifiedArguments(string decidedBy, string modifiedArgumentsJson, string? notes, DateTimeOffset now)
    {
        EnsurePending();
        Status = ApprovalStatus.Approved;
        WasModified = true;
        ModifiedArgumentsJson = modifiedArgumentsJson;
        Decide(decidedBy, notes, now);
    }

    public void Reject(string decidedBy, string? notes, DateTimeOffset now)
    {
        EnsurePending();
        Status = ApprovalStatus.Rejected;
        Decide(decidedBy, notes, now);
    }

    public void Expire(DateTimeOffset now)
    {
        EnsurePending();
        Status = ApprovalStatus.Expired;
        DecidedAt = now;
        DecidedBy = "system:timeout";
    }

    public bool IsExpired(DateTimeOffset now) => IsPending && now >= ExpiresAt;

    /// <summary>The effective arguments to execute: modified if provided, else the original.</summary>
    public string EffectiveArgumentsJson => WasModified && ModifiedArgumentsJson is not null
        ? ModifiedArgumentsJson
        : ProposedActionJson;

    private void Decide(string decidedBy, string? notes, DateTimeOffset now)
    {
        DecidedBy = decidedBy;
        DecisionNotes = notes;
        DecidedAt = now;
    }

    private void EnsurePending()
    {
        if (!IsPending) throw new InvalidOperationException($"Approval {Id} is already {Status}.");
    }
}
