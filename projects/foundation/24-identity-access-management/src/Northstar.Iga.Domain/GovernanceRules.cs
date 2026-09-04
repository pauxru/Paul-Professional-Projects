namespace Northstar.Iga.Domain;

public sealed record SoDViolation(
    Guid RuleId,
    string RuleName,
    Guid UserId,
    Guid EntitlementAId,
    Guid EntitlementBId,
    RiskRating Severity,
    bool HasActiveException,
    DateTimeOffset? ExceptionExpiresAt);

public static class SoDEngine
{
    public static IReadOnlyList<SoDViolation> Detect(
        Guid userId,
        IEnumerable<Guid> entitlementIds,
        IEnumerable<SoDRule> rules,
        IEnumerable<SoDException> exceptions,
        DateTimeOffset now)
    {
        var held = entitlementIds.ToHashSet();
        var activeExceptions = exceptions
            .Where(x => x.UserId == userId && x.ExpiresAt > now)
            .GroupBy(x => x.RuleId)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(y => y.ExpiresAt).First());

        return rules
            .Where(rule => held.Contains(rule.EntitlementAId) && held.Contains(rule.EntitlementBId))
            .Select(rule =>
            {
                activeExceptions.TryGetValue(rule.Id, out var exception);
                return new SoDViolation(
                    rule.Id,
                    rule.Name,
                    userId,
                    rule.EntitlementAId,
                    rule.EntitlementBId,
                    rule.Severity,
                    exception is not null,
                    exception?.ExpiresAt);
            })
            .ToArray();
    }
}

public static class ApprovalWorkflowStateMachine
{
    public static void ActivateFirstStage(AccessRequest request, IReadOnlyCollection<ApprovalStep> steps)
    {
        if (steps.Count == 0)
        {
            request.Status = AccessRequestStatus.Approved;
            return;
        }

        var firstStage = steps.Min(x => x.Stage);
        request.CurrentStage = firstStage;
        foreach (var step in steps.Where(x => x.Stage == firstStage))
        {
            step.Status = ApprovalStepStatus.Pending;
        }
    }

    public static bool Approve(
        AccessRequest request,
        IReadOnlyCollection<ApprovalStep> steps,
        Guid stepId,
        Guid actorId,
        string reason,
        DateTimeOffset now)
    {
        EnsurePending(request);
        var step = steps.SingleOrDefault(x => x.Id == stepId)
            ?? throw new DomainRuleException("Approval step was not found.");
        if (step.Status != ApprovalStepStatus.Pending || step.ApproverId != actorId)
        {
            throw new DomainRuleException("Only the current approver can approve an active step.");
        }

        step.Status = ApprovalStepStatus.Approved;
        step.DecidedAt = now;
        step.Reason = reason;

        var stageSteps = steps.Where(x => x.Stage == request.CurrentStage).ToArray();
        if (stageSteps.Any(x => x.Status != ApprovalStepStatus.Approved))
        {
            return false;
        }

        var nextStage = steps
            .Where(x => x.Stage > request.CurrentStage)
            .Select(x => (int?)x.Stage)
            .Min();
        if (nextStage is null)
        {
            request.Status = AccessRequestStatus.Approved;
            request.CompletedAt = now;
            return true;
        }

        request.CurrentStage = nextStage.Value;
        foreach (var next in steps.Where(x => x.Stage == nextStage.Value))
        {
            next.Status = ApprovalStepStatus.Pending;
        }

        return false;
    }

    public static void Reject(
        AccessRequest request,
        IReadOnlyCollection<ApprovalStep> steps,
        Guid stepId,
        Guid actorId,
        string reason,
        DateTimeOffset now)
    {
        EnsurePending(request);
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainRuleException("A rejection reason is required.");
        }

        var step = steps.SingleOrDefault(x => x.Id == stepId)
            ?? throw new DomainRuleException("Approval step was not found.");
        if (step.Status != ApprovalStepStatus.Pending || step.ApproverId != actorId)
        {
            throw new DomainRuleException("Only the current approver can reject an active step.");
        }

        step.Status = ApprovalStepStatus.Rejected;
        step.DecidedAt = now;
        step.Reason = reason;
        request.Status = AccessRequestStatus.Rejected;
        request.RejectionReason = reason;
        request.CompletedAt = now;
    }

    public static void Delegate(ApprovalStep step, Guid actorId, Guid delegateId)
    {
        if (step.Status != ApprovalStepStatus.Pending || step.ApproverId != actorId)
        {
            throw new DomainRuleException("Only the current approver can delegate an active step.");
        }

        if (delegateId == actorId)
        {
            throw new DomainRuleException("An approval cannot be delegated to the same approver.");
        }

        step.ApproverId = delegateId;
        step.WasDelegated = true;
    }

    public static int EscalateOverdue(
        AccessRequest request,
        IEnumerable<ApprovalStep> steps,
        DateTimeOffset now,
        Guid escalationApprover,
        TimeSpan nextSla)
    {
        if (request.Status != AccessRequestStatus.Pending)
        {
            return 0;
        }

        var count = 0;
        foreach (var step in steps.Where(x => x.Status == ApprovalStepStatus.Pending && x.DueAt <= now))
        {
            step.ApproverId = escalationApprover;
            step.WasEscalated = true;
            step.DueAt = now.Add(nextSla);
            count++;
        }

        return count;
    }

    private static void EnsurePending(AccessRequest request)
    {
        if (request.Status != AccessRequestStatus.Pending)
        {
            throw new DomainRuleException($"Request is '{request.Status}' and cannot be decided.");
        }
    }
}
