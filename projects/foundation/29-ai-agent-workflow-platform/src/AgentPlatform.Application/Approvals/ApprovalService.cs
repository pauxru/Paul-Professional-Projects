using AgentPlatform.Application.Abstractions;
using AgentPlatform.Application.Engine;
using AgentPlatform.Domain.Abstractions;
using AgentPlatform.Domain.Approvals;

namespace AgentPlatform.Application.Approvals;

/// <summary>How an approver decided.</summary>
public enum ApprovalDecision
{
    Approve,
    Reject,
    ModifyAndApprove,
}

public sealed record ApprovalCommand(
    string ApprovalId,
    ApprovalDecision Decision,
    string? Notes = null,
    string? ModifiedArgumentsJson = null);

public sealed record ApprovalOutcome(bool Applied, string? Error, RunResult? Run);

/// <summary>
/// Orchestrates human approval decisions: authorises the approver, records the audited decision on
/// the <see cref="ApprovalTask"/>, then resumes the paused run deterministically.
/// </summary>
public sealed class ApprovalService
{
    private readonly IApprovalStore _approvals;
    private readonly IUnitOfWork _unitOfWork;
    private readonly WorkflowEngine _engine;
    private readonly IClock _clock;

    public ApprovalService(IApprovalStore approvals, IUnitOfWork unitOfWork, WorkflowEngine engine, IClock clock)
    {
        _approvals = approvals;
        _unitOfWork = unitOfWork;
        _engine = engine;
        _clock = clock;
    }

    public Task<IReadOnlyList<ApprovalTask>> ListPendingAsync(string? tenantId, CancellationToken ct)
        => _approvals.ListPendingAsync(tenantId, ct);

    public Task<ApprovalTask?> GetAsync(string approvalId, CancellationToken ct)
        => _approvals.GetAsync(approvalId, ct);

    public async Task<ApprovalOutcome> DecideAsync(AgentCaller caller, ApprovalCommand command, CancellationToken ct)
    {
        if (!caller.HasScope("agents:approve"))
            return new ApprovalOutcome(false, "Caller lacks the 'agents:approve' scope.", null);

        var approval = await _approvals.GetAsync(command.ApprovalId, ct);
        if (approval is null)
            return new ApprovalOutcome(false, "Approval not found.", null);
        if (approval.TenantId != caller.TenantId)
            return new ApprovalOutcome(false, "Approval belongs to another tenant.", null);
        if (!approval.IsPending)
            return new ApprovalOutcome(false, $"Approval already {approval.Status}.", null);

        switch (command.Decision)
        {
            case ApprovalDecision.Approve:
                approval.Approve(caller.UserId, command.Notes, _clock.UtcNow);
                break;
            case ApprovalDecision.Reject:
                approval.Reject(caller.UserId, command.Notes, _clock.UtcNow);
                break;
            case ApprovalDecision.ModifyAndApprove:
                if (string.IsNullOrWhiteSpace(command.ModifiedArgumentsJson))
                    return new ApprovalOutcome(false, "Modified arguments are required for modify-and-approve.", null);
                approval.ApproveWithModifiedArguments(caller.UserId, command.ModifiedArgumentsJson!, command.Notes, _clock.UtcNow);
                break;
        }

        await _unitOfWork.SaveChangesAsync(ct);

        var run = await _engine.ContinueAfterApprovalAsync(approval.RunId, ct);
        return new ApprovalOutcome(true, null, run);
    }
}
