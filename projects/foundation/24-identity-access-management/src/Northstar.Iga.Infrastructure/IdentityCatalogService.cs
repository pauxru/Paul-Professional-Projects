using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Northstar.Iga.Application;
using Northstar.Iga.Domain;

namespace Northstar.Iga.Infrastructure;

public sealed partial class IgaService
{
    public async Task<PagedResult<UserIdentity>> GetUsersAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var total = await _db.Users.AsNoTracking().CountAsync(cancellationToken);
        var items = await _db.Users.AsNoTracking()
            .OrderBy(x => x.DisplayName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return new PagedResult<UserIdentity>(items, page, pageSize, total, (int)Math.Ceiling(total / (double)pageSize));
    }

    public Task<UserIdentity?> GetUserAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<UserIdentity> CreateUserAsync(
        CreateUserCommand command,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.EmployeeNumber) ||
            string.IsNullOrWhiteSpace(command.DisplayName) ||
            string.IsNullOrWhiteSpace(command.Email) ||
            !command.Email.Contains('@', StringComparison.Ordinal))
        {
            throw new DomainRuleException("Employee number, display name, and a valid email are required.");
        }

        if (command.Clearance is < 0 or > 10)
        {
            throw new DomainRuleException("Clearance must be between 0 and 10.");
        }

        if (await _db.Users.AnyAsync(
                x => x.EmployeeNumber == command.EmployeeNumber || x.Email == command.Email,
                cancellationToken))
        {
            throw new DomainRuleException("Employee number and email must be unique.");
        }

        var user = new UserIdentity
        {
            EmployeeNumber = command.EmployeeNumber.Trim(),
            DisplayName = command.DisplayName.Trim(),
            Email = command.Email.Trim().ToLowerInvariant(),
            Department = command.Department.Trim(),
            JobTitle = command.JobTitle.Trim(),
            ManagerId = command.ManagerId,
            Location = command.Location.Trim(),
            CostCentre = command.CostCentre.Trim(),
            EmploymentType = command.EmploymentType,
            Clearance = command.Clearance,
            StartDate = command.StartDate,
            EndDate = command.EndDate,
            Status = IdentityStatus.Pending
        };
        _db.Users.Add(user);
        await AppendAuditAsync(actor, "identity.created", "user", user.Id.ToString(), correlationId, null, user,
            new { user.EmployeeNumber, user.Department }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return user;
    }

    public Task<IReadOnlyList<Group>> GetGroupsAsync(CancellationToken cancellationToken) =>
        GetGroupsInternalAsync(cancellationToken);

    private async Task<IReadOnlyList<Group>> GetGroupsInternalAsync(CancellationToken cancellationToken) =>
        await _db.Groups.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken);

    public async Task<Group> CreateGroupAsync(
        CreateGroupCommand command,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (command.Type == GroupType.Dynamic)
        {
            var probe = new UserIdentity();
            _ = DynamicGroupRuleEvaluator.IsMatch(probe, command.DynamicRule);
        }

        var group = new Group
        {
            Key = command.Key.Trim(),
            Name = command.Name.Trim(),
            Description = command.Description.Trim(),
            Type = command.Type,
            DynamicRule = command.Type == GroupType.Dynamic ? command.DynamicRule : null
        };
        _db.Groups.Add(group);
        await AppendAuditAsync(actor, "group.created", "group", group.Id.ToString(), correlationId, null, group, null,
            cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return group;
    }

    public async Task AddGroupMemberAsync(
        Guid groupId,
        Guid userId,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var group = await _db.Groups.SingleOrDefaultAsync(x => x.Id == groupId, cancellationToken)
            ?? throw new KeyNotFoundException($"Group '{groupId}' was not found.");
        _ = await RequireUserAsync(userId, cancellationToken);
        if (group.Type == GroupType.Dynamic)
        {
            throw new DomainRuleException("Dynamic group membership is managed by its rule.");
        }

        if (!await _db.GroupMembers.AnyAsync(x => x.GroupId == groupId && x.UserId == userId, cancellationToken))
        {
            var member = new GroupMember
            {
                GroupId = groupId,
                UserId = userId,
                Source = GroupMembershipSource.Static,
                AddedAt = _clock.UtcNow
            };
            _db.GroupMembers.Add(member);
            await AppendAuditAsync(actor, "group.member.added", "group", groupId.ToString(), correlationId, null, member,
                new { userId }, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task AddGroupRoleAsync(
        Guid groupId,
        Guid roleId,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (!await _db.Groups.AnyAsync(x => x.Id == groupId, cancellationToken) ||
            !await _db.Roles.AnyAsync(x => x.Id == roleId, cancellationToken))
        {
            throw new KeyNotFoundException("Group or role was not found.");
        }

        if (!await _db.GroupRoles.AnyAsync(x => x.GroupId == groupId && x.RoleId == roleId, cancellationToken))
        {
            _db.GroupRoles.Add(new GroupRole { GroupId = groupId, RoleId = roleId });
            await AppendAuditAsync(actor, "group.role.added", "group", groupId.ToString(), correlationId, null,
                new { roleId }, null, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task ReevaluateDynamicGroupsAsync(
        Guid userId,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var user = await RequireUserAsync(userId, cancellationToken);
        var groups = await _db.Groups
            .Where(x => x.Type == GroupType.Dynamic)
            .ToListAsync(cancellationToken);
        var memberships = await _db.GroupMembers
            .Where(x => x.UserId == userId)
            .ToListAsync(cancellationToken);

        foreach (var group in groups)
        {
            var existing = memberships.SingleOrDefault(x => x.GroupId == group.Id);
            var matches = DynamicGroupRuleEvaluator.IsMatch(user, group.DynamicRule);
            if (matches && existing is null)
            {
                var member = new GroupMember
                {
                    GroupId = group.Id,
                    UserId = userId,
                    Source = GroupMembershipSource.Dynamic,
                    AddedAt = _clock.UtcNow
                };
                _db.GroupMembers.Add(member);
                await AppendAuditAsync(actor, "dynamic-group.member.added", "group", group.Id.ToString(), correlationId,
                    null, member, new { userId, group.DynamicRule }, cancellationToken);
            }
            else if (!matches && existing?.Source == GroupMembershipSource.Dynamic)
            {
                _db.GroupMembers.Remove(existing);
                await AppendAuditAsync(actor, "dynamic-group.member.removed", "group", group.Id.ToString(), correlationId,
                    existing, null, new { userId, group.DynamicRule }, cancellationToken);
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Role>> GetRolesAsync(CancellationToken cancellationToken) =>
        await _db.Roles.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken);

    public async Task<Role> CreateRoleAsync(
        CreateRoleCommand command,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (command.IsBirthright && string.IsNullOrWhiteSpace(command.BirthrightRule))
        {
            throw new DomainRuleException("Birthright roles require a policy rule.");
        }

        if (command.IsBirthright)
        {
            _ = DynamicGroupRuleEvaluator.IsMatch(new UserIdentity(), command.BirthrightRule);
        }

        var role = new Role
        {
            Key = command.Key.Trim(),
            Name = command.Name.Trim(),
            Description = command.Description.Trim(),
            IsBirthright = command.IsBirthright,
            BirthrightRule = command.IsBirthright ? command.BirthrightRule : null
        };
        _db.Roles.Add(role);
        await AppendAuditAsync(actor, "role.created", "role", role.Id.ToString(), correlationId, null, role, null,
            cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return role;
    }

    public async Task AddRoleEntitlementAsync(
        Guid roleId,
        Guid entitlementId,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (!await _db.Roles.AnyAsync(x => x.Id == roleId, cancellationToken) ||
            !await _db.Entitlements.AnyAsync(x => x.Id == entitlementId, cancellationToken))
        {
            throw new KeyNotFoundException("Role or entitlement was not found.");
        }

        if (!await _db.RoleEntitlements.AnyAsync(
                x => x.RoleId == roleId && x.EntitlementId == entitlementId,
                cancellationToken))
        {
            _db.RoleEntitlements.Add(new RoleEntitlement { RoleId = roleId, EntitlementId = entitlementId });
            await AppendAuditAsync(actor, "role.entitlement.added", "role", roleId.ToString(), correlationId, null,
                new { entitlementId }, null, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task AddRoleInheritanceAsync(
        Guid roleId,
        Guid inheritedRoleId,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (!await _db.Roles.AnyAsync(x => x.Id == roleId, cancellationToken) ||
            !await _db.Roles.AnyAsync(x => x.Id == inheritedRoleId, cancellationToken))
        {
            throw new KeyNotFoundException("Role was not found.");
        }

        var edges = await _db.RoleInheritances.AsNoTracking().ToListAsync(cancellationToken);
        var resolver = new RoleHierarchyResolver(edges);
        if (resolver.WouldCreateCycle(roleId, inheritedRoleId))
        {
            throw new DomainRuleException("Role inheritance would create a cycle.");
        }

        if (!edges.Any(x => x.RoleId == roleId && x.InheritedRoleId == inheritedRoleId))
        {
            _db.RoleInheritances.Add(new RoleInheritance { RoleId = roleId, InheritedRoleId = inheritedRoleId });
            await AppendAuditAsync(actor, "role.inheritance.added", "role", roleId.ToString(), correlationId, null,
                new { inheritedRoleId }, null, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<TargetApplication>> GetApplicationsAsync(CancellationToken cancellationToken) =>
        await _db.Applications.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken);

    public async Task<TargetApplication> CreateApplicationAsync(
        CreateApplicationCommand command,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var application = new TargetApplication
        {
            Key = command.Key.Trim().ToLowerInvariant(),
            Name = command.Name.Trim(),
            Description = command.Description.Trim(),
            OwnerUserId = command.OwnerUserId
        };
        _db.Applications.Add(application);
        await AppendAuditAsync(actor, "application.created", "application", application.Id.ToString(), correlationId,
            null, application, null, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return application;
    }

    public async Task<IReadOnlyList<Entitlement>> GetEntitlementsAsync(CancellationToken cancellationToken) =>
        await _db.Entitlements.AsNoTracking().OrderBy(x => x.Permission).ToListAsync(cancellationToken);

    public async Task<Entitlement> CreateEntitlementAsync(
        CreateEntitlementCommand command,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (!await _db.Applications.AnyAsync(x => x.Id == command.ApplicationId, cancellationToken))
        {
            throw new KeyNotFoundException($"Application '{command.ApplicationId}' was not found.");
        }

        if (string.IsNullOrWhiteSpace(command.BusinessDescription))
        {
            throw new DomainRuleException("A business-reviewer description is required.");
        }

        var entitlement = new Entitlement
        {
            ApplicationId = command.ApplicationId,
            Key = command.Key.Trim(),
            Permission = command.Permission.Trim().ToLowerInvariant(),
            OwnerUserId = command.OwnerUserId,
            Risk = command.Risk,
            BusinessDescription = command.BusinessDescription.Trim(),
            IsPrivileged = command.IsPrivileged
        };
        _db.Entitlements.Add(entitlement);
        await AppendAuditAsync(actor, "entitlement.created", "entitlement", entitlement.Id.ToString(), correlationId,
            null, entitlement, null, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return entitlement;
    }

    public async Task GrantEntitlementAsync(
        Guid userId,
        Guid entitlementId,
        GrantSource source,
        string reason,
        DateTimeOffset? expiresAt,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        _ = await RequireUserAsync(userId, cancellationToken);
        if (!await _db.Entitlements.AnyAsync(x => x.Id == entitlementId, cancellationToken))
        {
            throw new KeyNotFoundException($"Entitlement '{entitlementId}' was not found.");
        }

        var now = _clock.UtcNow;
        var current = await _db.UserEntitlementGrants
            .Where(x => x.UserId == userId && x.EntitlementId == entitlementId && x.RevokedAt == null)
            .ToListAsync(cancellationToken);
        if (current.Any(x => x.ExpiresAt is null || x.ExpiresAt > now))
        {
            return;
        }

        var exclusion = await _db.UserEntitlementExclusions
            .SingleOrDefaultAsync(x => x.UserId == userId && x.EntitlementId == entitlementId, cancellationToken);
        if (exclusion is not null)
        {
            _db.UserEntitlementExclusions.Remove(exclusion);
        }

        var grant = new UserEntitlementGrant
        {
            UserId = userId,
            EntitlementId = entitlementId,
            Source = source,
            GrantedAt = now,
            ExpiresAt = expiresAt,
            Reason = reason
        };
        _db.UserEntitlementGrants.Add(grant);
        await AppendAuditAsync(actor, "access.entitlement.granted", "user", userId.ToString(), correlationId, null, grant,
            new { entitlementId, source, reason, expiresAt }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task GrantRoleAsync(
        Guid userId,
        Guid roleId,
        GrantSource source,
        string reason,
        DateTimeOffset? expiresAt,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        _ = await RequireUserAsync(userId, cancellationToken);
        if (!await _db.Roles.AnyAsync(x => x.Id == roleId, cancellationToken))
        {
            throw new KeyNotFoundException($"Role '{roleId}' was not found.");
        }

        var now = _clock.UtcNow;
        var current = await _db.UserRoleGrants
            .Where(x => x.UserId == userId && x.RoleId == roleId && x.RevokedAt == null)
            .ToListAsync(cancellationToken);
        if (current.Any(x => x.ExpiresAt is null || x.ExpiresAt > now))
        {
            return;
        }

        var grant = new UserRoleGrant
        {
            UserId = userId,
            RoleId = roleId,
            Source = source,
            GrantedAt = now,
            ExpiresAt = expiresAt,
            Reason = reason
        };
        _db.UserRoleGrants.Add(grant);
        await AppendAuditAsync(actor, "access.role.granted", "user", userId.ToString(), correlationId, null, grant,
            new { roleId, source, reason, expiresAt }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<LifecycleWorkflow> ProcessJoinerAsync(
        Guid userId,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var user = await RequireUserAsync(userId, cancellationToken);
        var workflow = await StartWorkflowAsync(userId, "Joiner", correlationId, cancellationToken);

        await RunWorkflowStepAsync(workflow, "Activate identity", 1, async () =>
        {
            user.Activate();
            await _db.SaveChangesAsync(cancellationToken);
        }, actor, correlationId, cancellationToken);
        await RunWorkflowStepAsync(workflow, "Evaluate dynamic groups", 2,
            () => ReevaluateDynamicGroupsAsync(userId, actor, correlationId, cancellationToken),
            actor, correlationId, cancellationToken);
        await RunWorkflowStepAsync(workflow, "Grant birthright roles", 3,
            () => ReconcileBirthrightRolesAsync(user, null, actor, correlationId, cancellationToken),
            actor, correlationId, cancellationToken);
        await RunWorkflowStepAsync(workflow, "Provision target accounts", 4,
            () => ProvisionEffectiveApplicationsAsync(userId, ProvisioningOperation.Create, actor, correlationId, cancellationToken),
            actor, correlationId, cancellationToken);

        await CompleteWorkflowAsync(workflow, actor, correlationId, cancellationToken);
        return workflow;
    }

    public async Task<LifecycleWorkflow> ProcessMoverAsync(
        Guid userId,
        MoveUserCommand command,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var user = await RequireUserAsync(userId, cancellationToken);
        if (user.Status != IdentityStatus.Active)
        {
            throw new DomainRuleException("Only an active identity can run through the mover workflow.");
        }

        var before = new
        {
            user.Department,
            user.JobTitle,
            user.ManagerId,
            user.Location,
            user.CostCentre,
            user.EmploymentType,
            user.Clearance
        };
        var shouldRecertifyExistingAccess =
            !user.Department.Equals(command.Department, StringComparison.OrdinalIgnoreCase) ||
            user.ManagerId != command.ManagerId;
        var accessBeforeMove = shouldRecertifyExistingAccess
            ? await ResolveDerivationsAsync(userId, cancellationToken)
            : [];
        var oldBirthrightGrants = await _db.UserRoleGrants
            .Where(x => x.UserId == userId && x.Source == GrantSource.Birthright && x.RevokedAt == null)
            .ToListAsync(cancellationToken);
        var workflow = await StartWorkflowAsync(userId, "Mover", correlationId, cancellationToken);

        await RunWorkflowStepAsync(workflow, "Update employment attributes", 1, async () =>
        {
            user.Department = command.Department.Trim();
            user.JobTitle = command.JobTitle.Trim();
            user.ManagerId = command.ManagerId;
            user.Location = command.Location.Trim();
            user.CostCentre = command.CostCentre.Trim();
            user.EmploymentType = command.EmploymentType;
            user.Clearance = command.Clearance;
            user.Version++;
            await AppendAuditAsync(actor, "identity.moved", "user", userId.ToString(), correlationId, before, user,
                new { gracePeriodDays = command.GracePeriodDays ?? _lifecycleOptions.MoverGracePeriodDays },
                cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
        }, actor, correlationId, cancellationToken);
        await RunWorkflowStepAsync(workflow, "Re-evaluate dynamic groups", 2,
            () => ReevaluateDynamicGroupsAsync(userId, actor, correlationId, cancellationToken),
            actor, correlationId, cancellationToken);
        await RunWorkflowStepAsync(workflow, "Reconcile birthright access", 3,
            async () =>
            {
                await ReconcileBirthrightRolesAsync(
                    user,
                    oldBirthrightGrants,
                    actor,
                    correlationId,
                    cancellationToken,
                    command.GracePeriodDays);
                if (shouldRecertifyExistingAccess)
                {
                    await CreateOrMergeMoverRecertificationAsync(
                        user,
                        accessBeforeMove.Select(x => x.EntitlementId).Distinct().ToArray(),
                        command.GracePeriodDays ?? _lifecycleOptions.MoverGracePeriodDays,
                        actor,
                        correlationId,
                        cancellationToken);
                    await _db.SaveChangesAsync(cancellationToken);
                }
            },
            actor, correlationId, cancellationToken);
        await RunWorkflowStepAsync(workflow, "Update target accounts", 4,
            () => ProvisionEffectiveApplicationsAsync(userId, ProvisioningOperation.Update, actor, correlationId, cancellationToken),
            actor, correlationId, cancellationToken);

        await CompleteWorkflowAsync(workflow, actor, correlationId, cancellationToken);
        return workflow;
    }

    public async Task<LifecycleWorkflow> ProcessLeaverAsync(
        Guid userId,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var user = await RequireUserAsync(userId, cancellationToken);
        var workflow = await StartWorkflowAsync(userId, "Leaver", correlationId, cancellationToken);

        await RunWorkflowStepAsync(workflow, "Terminate identity and sessions", 1, async () =>
        {
            var before = JsonSerializer.Serialize(user);
            user.Terminate(_clock.UtcNow);
            await AppendAuditAsync(actor, "identity.terminated", "user", userId.ToString(), correlationId, before, user,
                new { sessionsTerminated = true }, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
        }, actor, correlationId, cancellationToken);
        await RunWorkflowStepAsync(workflow, "Revoke all effective access", 2, async () =>
        {
            var now = _clock.UtcNow;
            var roles = await _db.UserRoleGrants
                .Where(x => x.UserId == userId && x.RevokedAt == null)
                .ToListAsync(cancellationToken);
            var grants = await _db.UserEntitlementGrants
                .Where(x => x.UserId == userId && x.RevokedAt == null)
                .ToListAsync(cancellationToken);
            var elevations = await _db.Elevations
                .Where(x => x.UserId == userId &&
                            (x.Status == ElevationStatus.Active || x.Status == ElevationStatus.Pending))
                .ToListAsync(cancellationToken);
            var memberships = await _db.GroupMembers.Where(x => x.UserId == userId).ToListAsync(cancellationToken);
            foreach (var role in roles) role.RevokedAt = now;
            foreach (var grant in grants) grant.RevokedAt = now;
            foreach (var elevation in elevations)
            {
                elevation.Status = ElevationStatus.Revoked;
                elevation.RevokedAt = now;
            }
            _db.GroupMembers.RemoveRange(memberships);
            await AppendAuditAsync(actor, "access.leaver.revoked-all", "user", userId.ToString(), correlationId, null, null,
                new
                {
                    roleGrants = roles.Count,
                    entitlementGrants = grants.Count,
                    elevations = elevations.Count,
                    groupMemberships = memberships.Count
                }, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
        }, actor, correlationId, cancellationToken);
        await RunWorkflowStepAsync(workflow, "Disable target accounts", 3, async () =>
        {
            foreach (var connector in _connectors.Keys)
            {
                await ProvisionUserAsync(
                    userId,
                    connector,
                    ProvisioningOperation.Disable,
                    actor,
                    correlationId,
                    cancellationToken);
            }
        }, actor, correlationId, cancellationToken);
        await RunWorkflowStepAsync(workflow, "Detect residual orphan accounts", 4,
            () => DetectLeaverOrphansAsync(userId, actor, correlationId, cancellationToken),
            actor, correlationId, cancellationToken);

        await CompleteWorkflowAsync(workflow, actor, correlationId, cancellationToken);
        return workflow;
    }

    private async Task<LifecycleWorkflow> StartWorkflowAsync(
        Guid userId,
        string kind,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var workflow = new LifecycleWorkflow
        {
            UserId = userId,
            Kind = kind,
            CorrelationId = correlationId,
            CreatedAt = _clock.UtcNow
        };
        _db.LifecycleWorkflows.Add(workflow);
        await _db.SaveChangesAsync(cancellationToken);
        return workflow;
    }

    private async Task RunWorkflowStepAsync(
        LifecycleWorkflow workflow,
        string name,
        int order,
        Func<Task> action,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var step = new LifecycleWorkflowStep
        {
            WorkflowId = workflow.Id,
            Name = name,
            Order = order
        };
        _db.LifecycleWorkflowSteps.Add(step);
        await _db.SaveChangesAsync(cancellationToken);

        for (var attempt = 1; attempt <= _lifecycleOptions.StepMaxAttempts; attempt++)
        {
            step.Status = WorkflowStepStatus.Running;
            step.Attempts = attempt;
            step.StartedAt ??= _clock.UtcNow;
            try
            {
                await action();
                step.Status = WorkflowStepStatus.Succeeded;
                step.CompletedAt = _clock.UtcNow;
                step.LastError = null;
                await AppendAuditAsync(actor, "lifecycle.step.succeeded", "workflow", workflow.Id.ToString(), correlationId,
                    null, step, new { workflow.Kind, step.Name, attempt }, cancellationToken);
                await _db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (Exception exception) when (attempt < _lifecycleOptions.StepMaxAttempts)
            {
                step.Status = WorkflowStepStatus.Failed;
                step.LastError = exception.Message;
                await AppendAuditAsync(actor, "lifecycle.step.retry", "workflow", workflow.Id.ToString(), correlationId,
                    null, step, new { workflow.Kind, step.Name, attempt, exception.Message }, cancellationToken);
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                step.Status = WorkflowStepStatus.Failed;
                step.LastError = exception.Message;
                step.CompletedAt = _clock.UtcNow;
                workflow.Status = WorkflowStatus.Failed;
                workflow.CompletedAt = _clock.UtcNow;
                await AppendAuditAsync(actor, "lifecycle.step.failed", "workflow", workflow.Id.ToString(), correlationId,
                    null, step, new { workflow.Kind, step.Name, attempt, exception.Message }, cancellationToken);
                await _db.SaveChangesAsync(cancellationToken);
                throw;
            }
        }
    }

    private async Task CompleteWorkflowAsync(
        LifecycleWorkflow workflow,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        workflow.Status = WorkflowStatus.Completed;
        workflow.CompletedAt = _clock.UtcNow;
        await AppendAuditAsync(actor, $"lifecycle.{workflow.Kind.ToLowerInvariant()}.completed", "workflow",
            workflow.Id.ToString(), correlationId, null, workflow, new { workflow.UserId }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await RefreshTelemetryAsync(cancellationToken);
    }

    private async Task ReconcileBirthrightRolesAsync(
        UserIdentity user,
        IReadOnlyCollection<UserRoleGrant>? previousBirthrightGrants,
        string actor,
        string correlationId,
        CancellationToken cancellationToken,
        int? gracePeriodDays = null)
    {
        var roles = await _db.Roles.Where(x => x.IsBirthright).ToListAsync(cancellationToken);
        var matchingRoleIds = roles
            .Where(x => DynamicGroupRuleEvaluator.IsMatch(user, x.BirthrightRule))
            .Select(x => x.Id)
            .ToHashSet();
        var current = await _db.UserRoleGrants
            .Where(x => x.UserId == user.Id && x.Source == GrantSource.Birthright && x.RevokedAt == null)
            .ToListAsync(cancellationToken);
        var now = _clock.UtcNow;

        foreach (var roleId in matchingRoleIds)
        {
            if (current.Any(x => x.RoleId == roleId && (x.ExpiresAt is null || x.ExpiresAt > now)))
            {
                continue;
            }

            var grant = new UserRoleGrant
            {
                UserId = user.Id,
                RoleId = roleId,
                Source = GrantSource.Birthright,
                GrantedAt = now,
                Reason = "Birthright policy matched current employment attributes."
            };
            _db.UserRoleGrants.Add(grant);
            await AppendAuditAsync(actor, "birthright.role.granted", "user", user.Id.ToString(), correlationId, null, grant,
                new { roleId }, cancellationToken);
        }

        var removed = current.Where(x => !matchingRoleIds.Contains(x.RoleId)).ToArray();
        if (previousBirthrightGrants is not null && removed.Length > 0)
        {
            var graceDays = gracePeriodDays ?? _lifecycleOptions.MoverGracePeriodDays;
            foreach (var grant in removed)
            {
                grant.ExpiresAt = now.AddDays(graceDays);
            }

            await CreateMoverRecertificationAsync(
                user,
                removed,
                graceDays,
                actor,
                correlationId,
                cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task CreateMoverRecertificationAsync(
        UserIdentity user,
        IReadOnlyCollection<UserRoleGrant> removedGrants,
        int graceDays,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var roleIds = removedGrants.Select(x => x.RoleId).ToArray();
        var edges = await _db.RoleInheritances.AsNoTracking().ToListAsync(cancellationToken);
        var roleEntitlements = await _db.RoleEntitlements.AsNoTracking().ToListAsync(cancellationToken);
        var resolver = new RoleHierarchyResolver(edges);
        var entitlementIds = roleIds
            .SelectMany(x => resolver.ResolveEntitlementPaths(x, roleEntitlements))
            .Select(x => x.EntitlementId)
            .Distinct()
            .ToArray();
        if (entitlementIds.Length == 0)
        {
            return;
        }

        await CreateOrMergeMoverRecertificationAsync(
            user,
            entitlementIds,
            graceDays,
            actor,
            correlationId,
            cancellationToken);
    }

    private async Task CreateOrMergeMoverRecertificationAsync(
        UserIdentity user,
        IReadOnlyCollection<Guid> entitlementIds,
        int graceDays,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (entitlementIds.Count == 0)
        {
            return;
        }

        var entitlements = await _db.Entitlements.AsNoTracking()
            .Where(x => entitlementIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var fallbackReviewer = user.ManagerId ?? await _db.Users.AsNoTracking()
            .Where(x => x.Id != user.Id && x.Status == IdentityStatus.Active)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken) ?? user.Id;
        var campaign = await _db.Campaigns.SingleOrDefaultAsync(
            x => x.ScopeType == "mover" &&
                 x.ScopeValue == user.Id.ToString() &&
                 x.Status == CampaignStatus.Active,
            cancellationToken);
        var created = campaign is null;
        campaign ??= new CertificationCampaign
        {
            Name = $"Mover recertification - {user.DisplayName}",
            ScopeType = "mover",
            ScopeValue = user.Id.ToString(),
            ReviewerMode = ReviewerMode.Manager,
            Deadline = _clock.UtcNow.AddDays(graceDays),
            Status = CampaignStatus.Active,
            CreatedAt = _clock.UtcNow
        };
        if (created)
        {
            _db.Campaigns.Add(campaign);
        }
        var existingIds = (await _db.CertificationItems
                .Where(x => x.CampaignId == campaign.Id)
                .Select(x => x.EntitlementId)
                .ToListAsync(cancellationToken))
            .ToHashSet();
        var added = 0;
        foreach (var entitlementId in entitlementIds.Where(entitlements.ContainsKey))
        {
            if (!existingIds.Add(entitlementId))
            {
                continue;
            }
            _db.CertificationItems.Add(new CertificationItem
            {
                CampaignId = campaign.Id,
                UserId = user.Id,
                EntitlementId = entitlementId,
                ReviewerId = fallbackReviewer,
                DerivationJson = JsonSerializer.Serialize(new[]
                {
                    new[] { "pre-move-access", $"entitlement:{entitlements[entitlementId].Key}" }
                })
            });
            added++;
        }

        if (created || added > 0)
        {
            await AppendAuditAsync(actor, created ? "campaign.mover.created" : "campaign.mover.expanded", "campaign",
                campaign.Id.ToString(), correlationId, null, campaign,
                new { user.Id, itemCount = added, graceDays }, cancellationToken);
        }
    }

    private async Task ProvisionEffectiveApplicationsAsync(
        Guid userId,
        ProvisioningOperation operation,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var derivations = await ResolveDerivationsAsync(userId, cancellationToken);
        var entitlementIds = derivations.Select(x => x.EntitlementId).Distinct().ToArray();
        var connectorKeys = await (
            from entitlement in _db.Entitlements.AsNoTracking()
            join application in _db.Applications.AsNoTracking() on entitlement.ApplicationId equals application.Id
            where entitlementIds.Contains(entitlement.Id)
            select application.Key)
            .Distinct()
            .ToListAsync(cancellationToken);
        foreach (var connectorKey in connectorKeys.Where(x => _connectors.ContainsKey(x)))
        {
            await ProvisionUserAsync(userId, connectorKey, operation, actor, correlationId, cancellationToken);
        }
    }

    private async Task DetectLeaverOrphansAsync(
        Guid userId,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var reconciliation = await ReconcileAsync(null, actor, correlationId, cancellationToken);
        var residual = reconciliation.Orphans.Count(x => x.UserId == userId);
        await AppendAuditAsync(actor, "leaver.orphan-scan.completed", "user", userId.ToString(), correlationId, null, null,
            new { residualOrphans = residual }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }
}
