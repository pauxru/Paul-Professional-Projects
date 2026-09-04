using Microsoft.EntityFrameworkCore;
using Northstar.Iga.Application;
using Northstar.Iga.Domain;

namespace Northstar.Iga.Infrastructure;

public sealed partial class IgaService
{
    public async Task<IReadOnlyList<AccessRequest>> GetRequestsAsync(CancellationToken cancellationToken) =>
        (await _db.AccessRequests.AsNoTracking().ToListAsync(cancellationToken))
        .OrderByDescending(x => x.RequestedAt)
        .ToArray();

    public async Task<IReadOnlyList<ApprovalStep>> GetApprovalStepsAsync(
        Guid requestId,
        CancellationToken cancellationToken) =>
        await _db.ApprovalSteps.AsNoTracking()
            .Where(x => x.RequestId == requestId)
            .OrderBy(x => x.Stage)
            .ThenBy(x => x.Kind)
            .ToListAsync(cancellationToken);

    public async Task<AccessRequest> CreateAccessRequestAsync(
        CreateAccessRequestCommand command,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Justification) || command.Justification.Trim().Length < 10)
        {
            throw new DomainRuleException("An access request justification of at least 10 characters is required.");
        }

        if (command.DurationDays is <= 0 or > 365)
        {
            throw new DomainRuleException("Duration must be between 1 and 365 days when supplied.");
        }

        var user = await RequireUserAsync(command.UserId, cancellationToken);
        if (user.Status != IdentityStatus.Active)
        {
            throw new DomainRuleException("Access can only be requested for an active identity.");
        }

        if (command.TargetType == RequestTargetType.Group)
        {
            var group = await _db.Groups.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == command.TargetId, cancellationToken)
                ?? throw new KeyNotFoundException($"Group '{command.TargetId}' was not found.");
            if (group.Type == GroupType.Dynamic)
            {
                throw new DomainRuleException("Dynamic groups are attribute-driven and cannot be requested.");
            }
        }

        var targetEntitlementIds = await ResolveTargetEntitlementIdsAsync(
            command.TargetType,
            command.TargetId,
            cancellationToken);
        var existing = await ResolveDerivationsAsync(command.UserId, cancellationToken);
        var rules = await _db.SoDRules.AsNoTracking().ToListAsync(cancellationToken);
        var exceptions = await _db.SoDExceptions.AsNoTracking()
            .Where(x => x.UserId == command.UserId)
            .ToListAsync(cancellationToken);
        var preventiveViolations = SoDEngine.Detect(
            command.UserId,
            existing.Select(x => x.EntitlementId).Concat(targetEntitlementIds),
            rules,
            exceptions,
            _clock.UtcNow);
        var blocking = preventiveViolations.Where(x => !x.HasActiveException).ToArray();
        if (blocking.Length > 0)
        {
            throw new DomainRuleException(
                $"Preventive SoD control blocked the request: {string.Join(", ", blocking.Select(x => x.RuleName))}.");
        }

        var entitlements = await _db.Entitlements.AsNoTracking()
            .Where(x => targetEntitlementIds.Contains(x.Id))
            .ToListAsync(cancellationToken);
        var risk = entitlements.Count == 0 ? RiskRating.Low : entitlements.Max(x => x.Risk);
        var request = new AccessRequest
        {
            UserId = command.UserId,
            RequesterId = command.RequesterId,
            TargetType = command.TargetType,
            TargetId = command.TargetId,
            Justification = command.Justification.Trim(),
            DurationDays = command.DurationDays,
            RequestedAt = _clock.UtcNow,
            Risk = risk
        };
        _db.AccessRequests.Add(request);

        if (risk == RiskRating.Low)
        {
            request.Status = AccessRequestStatus.Approved;
            request.CompletedAt = _clock.UtcNow;
            await MaterializeAccessRequestAsync(request, actor, correlationId, cancellationToken);
            await AppendAuditAsync(actor, "access-request.auto-approved", "access-request", request.Id.ToString(),
                correlationId, null, request, new { request.Risk }, cancellationToken);
        }
        else
        {
            var steps = await BuildApprovalStepsAsync(request, user, entitlements, cancellationToken);
            _db.ApprovalSteps.AddRange(steps);
            ApprovalWorkflowStateMachine.ActivateFirstStage(request, steps);
            await AppendAuditAsync(actor, "access-request.submitted", "access-request", request.Id.ToString(),
                correlationId, null, request,
                new { request.Risk, steps = steps.Select(x => new { x.Stage, x.Kind, x.ApproverId }) },
                cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
        await RefreshTelemetryAsync(cancellationToken);
        return request;
    }

    public async Task<AccessRequest> ApproveRequestAsync(
        Guid requestId,
        Guid stepId,
        ApprovalDecisionCommand command,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var request = await _db.AccessRequests.SingleOrDefaultAsync(x => x.Id == requestId, cancellationToken)
            ?? throw new KeyNotFoundException($"Access request '{requestId}' was not found.");
        var steps = await _db.ApprovalSteps.Where(x => x.RequestId == requestId).ToListAsync(cancellationToken);
        var completed = ApprovalWorkflowStateMachine.Approve(
            request,
            steps,
            stepId,
            command.ActorId,
            command.Reason,
            _clock.UtcNow);
        await AppendAuditAsync(actor, "access-request.step.approved", "access-request", requestId.ToString(),
            correlationId, null, steps.Single(x => x.Id == stepId),
            new { command.ActorId, command.Reason, workflowCompleted = completed }, cancellationToken);
        if (completed)
        {
            await MaterializeAccessRequestAsync(request, actor, correlationId, cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
        await RefreshTelemetryAsync(cancellationToken);
        return request;
    }

    public async Task<AccessRequest> RejectRequestAsync(
        Guid requestId,
        Guid stepId,
        ApprovalDecisionCommand command,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var request = await _db.AccessRequests.SingleOrDefaultAsync(x => x.Id == requestId, cancellationToken)
            ?? throw new KeyNotFoundException($"Access request '{requestId}' was not found.");
        var steps = await _db.ApprovalSteps.Where(x => x.RequestId == requestId).ToListAsync(cancellationToken);
        ApprovalWorkflowStateMachine.Reject(
            request,
            steps,
            stepId,
            command.ActorId,
            command.Reason,
            _clock.UtcNow);
        await AppendAuditAsync(actor, "access-request.rejected", "access-request", requestId.ToString(), correlationId,
            null, request, new { command.ActorId, command.Reason }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await RefreshTelemetryAsync(cancellationToken);
        return request;
    }

    public async Task DelegateApprovalAsync(
        Guid requestId,
        Guid stepId,
        DelegateApprovalCommand command,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (!await _db.AccessRequests.AnyAsync(x => x.Id == requestId, cancellationToken))
        {
            throw new KeyNotFoundException($"Access request '{requestId}' was not found.");
        }

        var step = await _db.ApprovalSteps
            .SingleOrDefaultAsync(x => x.Id == stepId && x.RequestId == requestId, cancellationToken)
            ?? throw new KeyNotFoundException($"Approval step '{stepId}' was not found.");
        ApprovalWorkflowStateMachine.Delegate(step, command.ActorId, command.DelegateId);
        await AppendAuditAsync(actor, "access-request.step.delegated", "access-request", requestId.ToString(),
            correlationId, null, step, new { command.ActorId, command.DelegateId }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> EscalateOverdueApprovalsAsync(
        Guid escalationApproverId,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        _ = await RequireUserAsync(escalationApproverId, cancellationToken);
        var requests = await _db.AccessRequests
            .Where(x => x.Status == AccessRequestStatus.Pending)
            .ToListAsync(cancellationToken);
        var requestIds = requests.Select(x => x.Id).ToArray();
        var steps = await _db.ApprovalSteps
            .Where(x => requestIds.Contains(x.RequestId) && x.Status == ApprovalStepStatus.Pending)
            .ToListAsync(cancellationToken);
        var total = 0;
        foreach (var request in requests)
        {
            var count = ApprovalWorkflowStateMachine.EscalateOverdue(
                request,
                steps.Where(x => x.RequestId == request.Id),
                _clock.UtcNow,
                escalationApproverId,
                TimeSpan.FromHours(_governanceOptions.EscalatedApprovalSlaHours));
            if (count > 0)
            {
                total += count;
                await AppendAuditAsync(actor, "access-request.escalated", "access-request", request.Id.ToString(),
                    correlationId, null, null, new { count, escalationApproverId }, cancellationToken);
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        return total;
    }

    public async Task<IReadOnlyList<Elevation>> GetElevationsAsync(CancellationToken cancellationToken) =>
        (await _db.Elevations.AsNoTracking().ToListAsync(cancellationToken))
        .OrderByDescending(x => x.StartsAt)
        .ToArray();

    public async Task<Elevation> CreateElevationAsync(
        CreateElevationCommand command,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Justification) ||
            string.IsNullOrWhiteSpace(command.TicketReference))
        {
            throw new DomainRuleException("JIT elevation requires a justification and ticket reference.");
        }

        if (command.EndsAt <= command.StartsAt || command.EndsAt <= _clock.UtcNow)
        {
            throw new DomainRuleException("JIT elevation must have a future end time after its start time.");
        }

        var user = await RequireUserAsync(command.UserId, cancellationToken);
        if (user.Status != IdentityStatus.Active)
        {
            throw new DomainRuleException("Only an active identity can be elevated.");
        }

        var entitlement = await _db.Entitlements.SingleOrDefaultAsync(
                x => x.Id == command.EntitlementId,
                cancellationToken)
            ?? throw new KeyNotFoundException($"Entitlement '{command.EntitlementId}' was not found.");
        if (!entitlement.IsPrivileged)
        {
            throw new DomainRuleException("JIT elevation is reserved for entitlements marked as privileged.");
        }

        var elevation = new Elevation
        {
            UserId = command.UserId,
            EntitlementId = command.EntitlementId,
            Justification = command.Justification.Trim(),
            TicketReference = command.TicketReference.Trim(),
            StartsAt = command.StartsAt,
            EndsAt = command.EndsAt,
            RequiresApproval = command.RequiresApproval,
            SessionRecordingReference = command.SessionRecordingReference,
            Status = command.RequiresApproval ? ElevationStatus.Pending : ElevationStatus.Active
        };
        _db.Elevations.Add(elevation);
        await AppendAuditAsync(actor, "elevation.created", "elevation", elevation.Id.ToString(), correlationId, null,
            elevation, new { entitlement.Permission }, cancellationToken);
        await AppendAuditAsync(actor, "elevation.alert", "elevation", elevation.Id.ToString(), correlationId, null,
            elevation, new
            {
                severity = "high",
                command.UserId,
                command.EntitlementId,
                command.TicketReference,
                command.EndsAt
            }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await RefreshTelemetryAsync(cancellationToken);
        return elevation;
    }

    public async Task<Elevation> ApproveElevationAsync(
        Guid elevationId,
        Guid approverId,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var elevation = await _db.Elevations.SingleOrDefaultAsync(x => x.Id == elevationId, cancellationToken)
            ?? throw new KeyNotFoundException($"Elevation '{elevationId}' was not found.");
        if (elevation.Status != ElevationStatus.Pending)
        {
            throw new DomainRuleException("Only a pending elevation can be approved.");
        }

        _ = await RequireUserAsync(approverId, cancellationToken);
        elevation.Status = ElevationStatus.Active;
        elevation.ApprovedBy = approverId;
        await AppendAuditAsync(actor, "elevation.approved", "elevation", elevationId.ToString(), correlationId, null,
            elevation, new { approverId }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await RefreshTelemetryAsync(cancellationToken);
        return elevation;
    }

    public async Task<Elevation> RevokeElevationAsync(
        Guid elevationId,
        string reason,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainRuleException("An early-revocation reason is required.");
        }

        var elevation = await _db.Elevations.SingleOrDefaultAsync(x => x.Id == elevationId, cancellationToken)
            ?? throw new KeyNotFoundException($"Elevation '{elevationId}' was not found.");
        if (elevation.Status is ElevationStatus.Expired or ElevationStatus.Revoked or ElevationStatus.Rejected)
        {
            throw new DomainRuleException($"Elevation is already '{elevation.Status}'.");
        }

        elevation.Status = ElevationStatus.Revoked;
        elevation.RevokedAt = _clock.UtcNow;
        await AppendAuditAsync(actor, "elevation.revoked", "elevation", elevationId.ToString(), correlationId, null,
            elevation, new { reason }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await RefreshTelemetryAsync(cancellationToken);
        return elevation;
    }

    public async Task<int> ExpireElevationsAsync(
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var candidates = await _db.Elevations
            .Where(x => x.Status == ElevationStatus.Active)
            .ToListAsync(cancellationToken);
        var expired = candidates.Where(x => x.EndsAt <= _clock.UtcNow).ToArray();
        foreach (var elevation in expired)
        {
            elevation.Status = ElevationStatus.Expired;
            await AppendAuditAsync(actor, "elevation.expired", "elevation", elevation.Id.ToString(), correlationId, null,
                elevation, new { elevation.EndsAt }, cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
        await RefreshTelemetryAsync(cancellationToken);
        return expired.Length;
    }

    private async Task<IReadOnlyList<ApprovalStep>> BuildApprovalStepsAsync(
        AccessRequest request,
        UserIdentity user,
        IReadOnlyCollection<Entitlement> entitlements,
        CancellationToken cancellationToken)
    {
        var securityApprover = await FindSecurityApproverAsync(user.Id, cancellationToken);
        var manager = user.ManagerId ?? securityApprover;
        var due = _clock.UtcNow.AddHours(_governanceOptions.ApprovalSlaHours);
        var steps = new List<ApprovalStep>
        {
            NewApprovalStep(request.Id, 1, "Manager", manager, due)
        };

        var owners = entitlements
            .Select(x => x.OwnerUserId)
            .Where(x => x is not null)
            .Select(x => x!.Value)
            .Distinct()
            .ToArray();
        if (owners.Length == 0)
        {
            owners = [manager];
        }

        steps.AddRange(owners.Select(owner => NewApprovalStep(request.Id, 2, "EntitlementOwner", owner, due)));
        if (request.Risk >= RiskRating.High)
        {
            steps.Add(NewApprovalStep(request.Id, 3, "Security", securityApprover, due));
        }

        return steps;
    }

    private static ApprovalStep NewApprovalStep(
        Guid requestId,
        int stage,
        string kind,
        Guid approver,
        DateTimeOffset due) =>
        new()
        {
            RequestId = requestId,
            Stage = stage,
            Kind = kind,
            ApproverId = approver,
            OriginalApproverId = approver,
            DueAt = due
        };

    private async Task<Guid> FindSecurityApproverAsync(Guid excludedUserId, CancellationToken cancellationToken)
    {
        var security = await _db.Users.AsNoTracking()
            .Where(x => x.Id != excludedUserId && x.Status == IdentityStatus.Active &&
                        x.JobTitle.Contains("Security"))
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return security ?? await _db.Users.AsNoTracking()
            .Where(x => x.Id != excludedUserId && x.Status == IdentityStatus.Active)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new DomainRuleException("No active approver is available.");
    }

    private async Task MaterializeAccessRequestAsync(
        AccessRequest request,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        DateTimeOffset? expiresAt = request.DurationDays is null
            ? null
            : _clock.UtcNow.AddDays(request.DurationDays.Value);
        if (request.TargetType == RequestTargetType.Entitlement)
        {
            var exclusion = await _db.UserEntitlementExclusions.SingleOrDefaultAsync(
                x => x.UserId == request.UserId && x.EntitlementId == request.TargetId,
                cancellationToken);
            if (exclusion is not null) _db.UserEntitlementExclusions.Remove(exclusion);
            if (!await _db.UserEntitlementGrants.AnyAsync(
                    x => x.UserId == request.UserId && x.EntitlementId == request.TargetId && x.RevokedAt == null,
                    cancellationToken))
            {
                _db.UserEntitlementGrants.Add(new UserEntitlementGrant
                {
                    UserId = request.UserId,
                    EntitlementId = request.TargetId,
                    Source = GrantSource.Request,
                    GrantedAt = _clock.UtcNow,
                    ExpiresAt = expiresAt,
                    Reason = request.Justification
                });
            }
        }
        else if (request.TargetType == RequestTargetType.Role)
        {
            if (!await _db.UserRoleGrants.AnyAsync(
                    x => x.UserId == request.UserId && x.RoleId == request.TargetId && x.RevokedAt == null,
                    cancellationToken))
            {
                _db.UserRoleGrants.Add(new UserRoleGrant
                {
                    UserId = request.UserId,
                    RoleId = request.TargetId,
                    Source = GrantSource.Request,
                    GrantedAt = _clock.UtcNow,
                    ExpiresAt = expiresAt,
                    Reason = request.Justification
                });
            }
        }
        else if (!await _db.GroupMembers.AnyAsync(
                     x => x.UserId == request.UserId && x.GroupId == request.TargetId,
                     cancellationToken))
        {
            _db.GroupMembers.Add(new GroupMember
            {
                UserId = request.UserId,
                GroupId = request.TargetId,
                Source = GroupMembershipSource.Static,
                AddedAt = _clock.UtcNow
            });
        }

        request.Status = AccessRequestStatus.Fulfilled;
        request.CompletedAt = _clock.UtcNow;
        await AppendAuditAsync(actor, "access-request.fulfilled", "access-request", request.Id.ToString(), correlationId,
            null, request, new { request.TargetType, request.TargetId, expiresAt }, cancellationToken);
    }
}
