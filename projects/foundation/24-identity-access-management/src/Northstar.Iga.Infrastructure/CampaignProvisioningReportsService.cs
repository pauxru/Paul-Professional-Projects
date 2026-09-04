using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Northstar.Iga.Application;
using Northstar.Iga.Domain;

namespace Northstar.Iga.Infrastructure;

public sealed partial class IgaService
{
    public async Task<IReadOnlyList<CertificationCampaign>> GetCampaignsAsync(CancellationToken cancellationToken) =>
        (await _db.Campaigns.AsNoTracking().ToListAsync(cancellationToken))
        .OrderByDescending(x => x.CreatedAt)
        .ToArray();

    public async Task<CertificationCampaign> CreateCampaignAsync(
        CreateCampaignCommand command,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (command.Deadline <= _clock.UtcNow)
        {
            throw new DomainRuleException("Certification campaign deadline must be in the future.");
        }

        var scopeType = command.ScopeType.Trim().ToLowerInvariant();
        if (scopeType is not ("application" or "department" or "risk"))
        {
            throw new DomainRuleException("Campaign scope must be application, department, or risk.");
        }

        var users = await _db.Users.AsNoTracking()
            .Where(x => x.Status == IdentityStatus.Active)
            .ToListAsync(cancellationToken);
        if (scopeType == "department")
        {
            users = users.Where(x => x.Department.Equals(command.ScopeValue, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var entitlements = await _db.Entitlements.AsNoTracking().ToListAsync(cancellationToken);
        var entitlementById = entitlements.ToDictionary(x => x.Id);
        var applications = await _db.Applications.AsNoTracking().ToListAsync(cancellationToken);
        Guid? scopedApplicationId = null;
        RiskRating? scopedRisk = null;
        if (scopeType == "application")
        {
            scopedApplicationId = applications
                .FirstOrDefault(x => x.Id.ToString().Equals(command.ScopeValue, StringComparison.OrdinalIgnoreCase) ||
                                     x.Key.Equals(command.ScopeValue, StringComparison.OrdinalIgnoreCase))
                ?.Id ?? throw new KeyNotFoundException($"Application scope '{command.ScopeValue}' was not found.");
        }
        else if (scopeType == "risk")
        {
            if (!Enum.TryParse<RiskRating>(command.ScopeValue, true, out var risk))
            {
                throw new DomainRuleException($"Risk scope '{command.ScopeValue}' is invalid.");
            }

            scopedRisk = risk;
        }

        var campaign = new CertificationCampaign
        {
            Name = command.Name.Trim(),
            ScopeType = scopeType,
            ScopeValue = command.ScopeValue.Trim(),
            ReviewerMode = command.ReviewerMode,
            Deadline = command.Deadline,
            Status = CampaignStatus.Active,
            CreatedAt = _clock.UtcNow
        };
        _db.Campaigns.Add(campaign);
        var itemCount = 0;
        foreach (var user in users)
        {
            var profile = await GetUserAccessProfileAsync(user.Id, cancellationToken);
            foreach (var access in profile.Access)
            {
                var entitlement = entitlementById[access.EntitlementId];
                if (scopedApplicationId is not null && entitlement.ApplicationId != scopedApplicationId ||
                    scopedRisk is not null && entitlement.Risk != scopedRisk)
                {
                    continue;
                }

                var reviewer = command.ReviewerMode == ReviewerMode.Manager
                    ? user.ManagerId
                    : entitlement.OwnerUserId;
                reviewer ??= user.ManagerId;
                reviewer ??= await FindSecurityApproverAsync(user.Id, cancellationToken);
                _db.CertificationItems.Add(new CertificationItem
                {
                    CampaignId = campaign.Id,
                    UserId = user.Id,
                    EntitlementId = entitlement.Id,
                    ReviewerId = reviewer.Value,
                    DerivationJson = JsonSerializer.Serialize(access.DerivationPaths)
                });
                itemCount++;
            }
        }

        if (itemCount == 0)
        {
            campaign.Status = CampaignStatus.Completed;
            campaign.CompletedAt = _clock.UtcNow;
        }

        await AppendAuditAsync(actor, "campaign.created", "campaign", campaign.Id.ToString(), correlationId, null,
            campaign, new { itemCount, command.ScopeType, command.ScopeValue, command.ReviewerMode }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await RefreshTelemetryAsync(cancellationToken);
        return campaign;
    }

    public async Task<IReadOnlyList<CertificationItem>> GetCampaignItemsAsync(
        Guid campaignId,
        CancellationToken cancellationToken) =>
        await _db.CertificationItems.AsNoTracking()
            .Where(x => x.CampaignId == campaignId)
            .OrderBy(x => x.UserId)
            .ThenBy(x => x.EntitlementId)
            .ToListAsync(cancellationToken);

    public async Task<CampaignProgress> CertifyItemsAsync(
        Guid campaignId,
        CertificationCommand command,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (command.Decision is not (CertificationDecision.Approved or CertificationDecision.Revoked))
        {
            throw new DomainRuleException("Reviewers may only approve or revoke certification items.");
        }

        if (string.IsNullOrWhiteSpace(command.Justification))
        {
            throw new DomainRuleException("Certification decisions require a justification.");
        }

        var campaign = await _db.Campaigns.SingleOrDefaultAsync(x => x.Id == campaignId, cancellationToken)
            ?? throw new KeyNotFoundException($"Campaign '{campaignId}' was not found.");
        if (campaign.Status is CampaignStatus.Completed or CampaignStatus.Overdue)
        {
            throw new DomainRuleException($"Campaign is already '{campaign.Status}'.");
        }

        var allItems = await _db.CertificationItems
            .Where(x => x.CampaignId == campaignId)
            .ToListAsync(cancellationToken);
        var itemIds = command.ItemIds.Distinct().ToHashSet();
        var items = allItems.Where(x => itemIds.Contains(x.Id)).ToArray();
        if (items.Length != itemIds.Count)
        {
            throw new KeyNotFoundException("One or more certification items were not found in this campaign.");
        }

        foreach (var item in items)
        {
            if (item.Decision != CertificationDecision.Pending)
            {
                throw new DomainRuleException($"Certification item '{item.Id}' was already decided.");
            }
            if (item.ReviewerId != command.ReviewerId)
            {
                throw new DomainRuleException($"Reviewer '{command.ReviewerId}' is not assigned item '{item.Id}'.");
            }

            item.Decision = command.Decision;
            item.Justification = command.Justification.Trim();
            item.DecidedAt = _clock.UtcNow;
            if (command.Decision == CertificationDecision.Revoked)
            {
                await ApplyCertificationRevocationAsync(item, command.Justification, cancellationToken);
            }

            await AppendAuditAsync(actor, $"certification.{command.Decision.ToString().ToLowerInvariant()}",
                "certification-item", item.Id.ToString(), correlationId, null, item,
                new { command.ReviewerId, command.Justification }, cancellationToken);
        }

        if (allItems.All(x => x.Decision != CertificationDecision.Pending))
        {
            campaign.Status = CampaignStatus.Completed;
            campaign.CompletedAt = _clock.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);
        await RefreshTelemetryAsync(cancellationToken);
        return CalculateProgress(campaignId, allItems);
    }

    public async Task<int> AutoRevokeOverdueCampaignsAsync(
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var campaigns = await _db.Campaigns
            .Where(x => x.Status == CampaignStatus.Active)
            .ToListAsync(cancellationToken);
        var overdue = campaigns.Where(x => x.Deadline <= _clock.UtcNow).ToArray();
        var revoked = 0;
        foreach (var campaign in overdue)
        {
            var items = await _db.CertificationItems
                .Where(x => x.CampaignId == campaign.Id && x.Decision == CertificationDecision.Pending)
                .ToListAsync(cancellationToken);
            foreach (var item in items)
            {
                item.Decision = CertificationDecision.AutoRevoked;
                item.Justification = "Automatically revoked because the certification deadline elapsed.";
                item.DecidedAt = _clock.UtcNow;
                await ApplyCertificationRevocationAsync(item, item.Justification, cancellationToken);
                await AppendAuditAsync(actor, "certification.auto-revoked", "certification-item", item.Id.ToString(),
                    correlationId, null, item, new { campaign.Deadline }, cancellationToken);
                revoked++;
            }

            campaign.Status = CampaignStatus.Overdue;
            campaign.CompletedAt = _clock.UtcNow;
            await AppendAuditAsync(actor, "campaign.overdue.completed", "campaign", campaign.Id.ToString(),
                correlationId, null, campaign, new { autoRevoked = items.Count }, cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
        await RefreshTelemetryAsync(cancellationToken);
        return revoked;
    }

    public async Task<CampaignProgress> GetCampaignProgressAsync(
        Guid campaignId,
        CancellationToken cancellationToken)
    {
        if (!await _db.Campaigns.AnyAsync(x => x.Id == campaignId, cancellationToken))
        {
            throw new KeyNotFoundException($"Campaign '{campaignId}' was not found.");
        }

        var items = await _db.CertificationItems.AsNoTracking()
            .Where(x => x.CampaignId == campaignId)
            .ToListAsync(cancellationToken);
        return CalculateProgress(campaignId, items);
    }

    public async Task<ProvisioningResult> ProvisionUserAsync(
        Guid userId,
        string connectorKey,
        ProvisioningOperation operation,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (!_connectors.TryGetValue(connectorKey, out var connector))
        {
            throw new KeyNotFoundException($"Provisioning connector '{connectorKey}' was not found.");
        }

        var user = await RequireUserAsync(userId, cancellationToken);
        var application = await _db.Applications.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Key == connectorKey, cancellationToken)
            ?? throw new KeyNotFoundException($"Application catalogue entry for connector '{connectorKey}' was not found.");
        var job = new ProvisioningJob
        {
            ConnectorKey = connectorKey,
            UserId = userId,
            Operation = operation,
            RequestedAt = _clock.UtcNow
        };
        _db.ProvisioningJobs.Add(job);
        await _db.SaveChangesAsync(cancellationToken);

        Exception? lastException = null;
        for (var attempt = 1; attempt <= _provisioningOptions.MaxAttempts; attempt++)
        {
            job.Attempts = attempt;
            try
            {
                ConnectorAccountSnapshot? snapshot = null;
                if (operation is ProvisioningOperation.Create or ProvisioningOperation.Update)
                {
                    var access = await ResolveDerivationsAsync(userId, cancellationToken);
                    var entitlementIds = access.Select(x => x.EntitlementId).Distinct().ToArray();
                    var permissions = await _db.Entitlements.AsNoTracking()
                        .Where(x => x.ApplicationId == application.Id && entitlementIds.Contains(x.Id))
                        .Select(x => x.Permission)
                        .ToListAsync(cancellationToken);
                    var connectorUser = new ConnectorUser(
                        user.Id,
                        user.Email,
                        user.DisplayName,
                        user.Status == IdentityStatus.Active,
                        permissions);
                    snapshot = operation == ProvisioningOperation.Create
                        ? await connector.CreateAsync(connectorUser, cancellationToken)
                        : await connector.UpdateAsync(connectorUser, cancellationToken);
                }
                else if (operation == ProvisioningOperation.Disable)
                {
                    await connector.DisableAsync(userId, cancellationToken);
                    snapshot = (await connector.GetAccountsAsync(cancellationToken))
                        .FirstOrDefault(x => x.UserId == userId);
                }
                else
                {
                    await connector.DeleteAsync(userId, cancellationToken);
                }

                if (snapshot is not null)
                {
                    await UpsertProvisioningAccountAsync(application.Id, connectorKey, snapshot, cancellationToken);
                }
                else if (operation == ProvisioningOperation.Delete)
                {
                    var account = await _db.ProvisioningAccounts.SingleOrDefaultAsync(
                        x => x.ConnectorKey == connectorKey && x.UserId == userId,
                        cancellationToken);
                    if (account is not null) _db.ProvisioningAccounts.Remove(account);
                }

                job.Status = ProvisioningJobStatus.Succeeded;
                job.CompletedAt = _clock.UtcNow;
                job.LastError = null;
                await AppendAuditAsync(actor, $"provisioning.{operation.ToString().ToLowerInvariant()}.succeeded",
                    "provisioning-job", job.Id.ToString(), correlationId, null, job,
                    new { connectorKey, userId, attempts = attempt }, cancellationToken);
                await _db.SaveChangesAsync(cancellationToken);
                return new ProvisioningResult(job.Id, job.Status, job.Attempts, null);
            }
            catch (Exception exception) when (
                exception is ProvisioningTransientException && attempt < _provisioningOptions.MaxAttempts)
            {
                lastException = exception;
                job.LastError = exception.Message;
                _logger.LogWarning(
                    exception,
                    "Provisioning attempt {Attempt} for job {JobId} failed transiently",
                    attempt,
                    job.Id);
                await _db.SaveChangesAsync(cancellationToken);
                if (_provisioningOptions.RetryDelayMilliseconds > 0)
                {
                    await Task.Delay(_provisioningOptions.RetryDelayMilliseconds, cancellationToken);
                }
            }
            catch (Exception exception)
            {
                lastException = exception;
                break;
            }
        }

        job.Status = ProvisioningJobStatus.Quarantined;
        job.LastError = lastException?.Message ?? "Provisioning failed without an error detail.";
        job.CompletedAt = _clock.UtcNow;
        var quarantine = new ProvisioningQuarantineItem
        {
            JobId = job.Id,
            ConnectorKey = connectorKey,
            PayloadJson = JsonSerializer.Serialize(new { userId, operation }),
            Reason = job.LastError,
            CreatedAt = _clock.UtcNow
        };
        _db.ProvisioningQuarantine.Add(quarantine);
        await AppendAuditAsync(actor, "provisioning.quarantined", "provisioning-job", job.Id.ToString(), correlationId,
            null, job, new { connectorKey, userId, operation, job.Attempts, job.LastError }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return new ProvisioningResult(job.Id, job.Status, job.Attempts, job.LastError);
    }

    public async Task<int> ImportHrIdentitiesAsync(
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var records = await _hrSource.ReadAsync(cancellationToken);
        var imported = 0;
        foreach (var record in records)
        {
            if (await _db.Users.AnyAsync(x => x.EmployeeNumber == record.EmployeeNumber, cancellationToken))
            {
                continue;
            }

            if (!Enum.TryParse<EmploymentType>(record.EmploymentType, true, out var employmentType))
            {
                employmentType = EmploymentType.Employee;
            }
            var user = await CreateUserAsync(new CreateUserCommand(
                record.EmployeeNumber,
                record.DisplayName,
                record.Email,
                record.Department,
                record.JobTitle,
                null,
                record.Location,
                record.CostCentre,
                employmentType,
                record.Clearance,
                record.StartDate), actor, correlationId, cancellationToken);
            await ProcessJoinerAsync(user.Id, actor, correlationId, cancellationToken);
            imported++;
        }

        return imported;
    }

    public async Task<ReconciliationResult> ReconcileAsync(
        string? connectorKey,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        IReadOnlyCollection<IProvisioningConnector> selected;
        if (connectorKey is null)
        {
            selected = _connectors.Values.ToArray();
        }
        else if (_connectors.TryGetValue(connectorKey, out var selectedConnector))
        {
            selected = [selectedConnector];
        }
        else
        {
            throw new KeyNotFoundException($"Provisioning connector '{connectorKey}' was not found.");
        }
        var orphans = new List<ReconciliationIssue>();
        var rogueGrants = new List<ReconciliationIssue>();
        foreach (var targetConnector in selected)
        {
            var application = await _db.Applications.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Key == targetConnector.Key, cancellationToken)
                ?? throw new KeyNotFoundException($"Application catalogue entry for connector '{targetConnector.Key}' was not found.");
            var accounts = await targetConnector.GetAccountsAsync(cancellationToken);
            foreach (var account in accounts)
            {
                var user = account.UserId is null
                    ? null
                    : await _db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == account.UserId, cancellationToken);
                var isOrphan = user is null || user.Status == IdentityStatus.Terminated && account.Enabled;
                if (isOrphan)
                {
                    orphans.Add(new ReconciliationIssue(
                        targetConnector.Key,
                        "OrphanAccount",
                        account.ExternalId,
                        account.UserId,
                        "Target account has no active authoritative identity."));
                }

                var persisted = await UpsertProvisioningAccountAsync(
                    application.Id,
                    targetConnector.Key,
                    account,
                    cancellationToken,
                    isOrphan,
                    false);
                var expectedPermissions = user is null
                    ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    : (await GetExpectedPermissionsForApplicationAsync(user.Id, application.Id, cancellationToken))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var permission in account.Permissions.Where(x => !expectedPermissions.Contains(x)))
                {
                    rogueGrants.Add(new ReconciliationIssue(
                        targetConnector.Key,
                        "RogueGrant",
                        account.ExternalId,
                        account.UserId,
                        $"Permission '{permission}' exists in the target but has no IGA derivation."));
                }

                var oldGrants = await _db.ProvisioningGrants
                    .Where(x => x.ProvisioningAccountId == persisted.Id)
                    .ToListAsync(cancellationToken);
                _db.ProvisioningGrants.RemoveRange(oldGrants);
                foreach (var permission in account.Permissions.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var entitlement = await _db.Entitlements.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.Permission == permission, cancellationToken);
                    _db.ProvisioningGrants.Add(new ProvisioningGrant
                    {
                        ProvisioningAccountId = persisted.Id,
                        EntitlementId = entitlement?.Id,
                        Permission = permission,
                        IsRogue = !expectedPermissions.Contains(permission)
                    });
                }
            }
        }

        await AppendAuditAsync(actor, "provisioning.reconciled", "provisioning", connectorKey ?? "all", correlationId,
            null, null, new { orphans = orphans.Count, rogueGrants = rogueGrants.Count }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return new ReconciliationResult(orphans, rogueGrants);
    }

    public async Task<IReadOnlyList<ProvisioningQuarantineItem>> GetQuarantineAsync(
        CancellationToken cancellationToken) =>
        (await _db.ProvisioningQuarantine.AsNoTracking()
            .Where(x => x.ResolvedAt == null)
            .ToListAsync(cancellationToken))
        .OrderByDescending(x => x.CreatedAt)
        .ToArray();

    public async Task<UserAccessProfile> GetUserAccessProfileAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var user = await RequireUserAsync(userId, cancellationToken);
        var derivations = await ResolveDerivationsAsync(userId, cancellationToken);
        var entitlementIds = derivations.Select(x => x.EntitlementId).Distinct().ToArray();
        var entitlements = await _db.Entitlements.AsNoTracking()
            .Where(x => entitlementIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var access = derivations
            .GroupBy(x => x.EntitlementId)
            .Where(x => entitlements.ContainsKey(x.Key))
            .Select(group =>
            {
                var entitlement = entitlements[group.Key];
                return new AccessProfileEntry(
                    entitlement.Id,
                    entitlement.Key,
                    entitlement.Permission,
                    entitlement.Risk,
                    entitlement.IsPrivileged,
                    group.Select(x => x.Path).ToArray());
            })
            .OrderBy(x => x.Permission)
            .ToArray();
        return new UserAccessProfile(user.Id, user.DisplayName, access);
    }

    public async Task<IReadOnlyList<object>> GetEntitlementHoldersReportAsync(
        CancellationToken cancellationToken)
    {
        var users = await _db.Users.AsNoTracking()
            .Where(x => x.Status == IdentityStatus.Active)
            .ToListAsync(cancellationToken);
        var rows = new List<object>();
        foreach (var user in users)
        {
            var profile = await GetUserAccessProfileAsync(user.Id, cancellationToken);
            rows.AddRange(profile.Access.Select(x => (object)new
            {
                x.EntitlementId,
                x.EntitlementKey,
                x.Permission,
                userId = user.Id,
                user.DisplayName,
                user.Department,
                derivationCount = x.DerivationPaths.Count
            }));
        }

        return rows;
    }

    public async Task<IReadOnlyList<object>> GetPrivilegedInventoryReportAsync(
        CancellationToken cancellationToken)
    {
        var users = await _db.Users.AsNoTracking()
            .Where(x => x.Status == IdentityStatus.Active)
            .ToListAsync(cancellationToken);
        var rows = new List<object>();
        foreach (var user in users)
        {
            var profile = await GetUserAccessProfileAsync(user.Id, cancellationToken);
            rows.AddRange(profile.Access.Where(x => x.Privileged).Select(x => (object)new
            {
                userId = user.Id,
                user.DisplayName,
                user.Department,
                x.EntitlementId,
                x.Permission,
                x.Risk,
                x.DerivationPaths
            }));
        }

        return rows;
    }

    public async Task<IReadOnlyList<object>> GetDormantAccountsReportAsync(
        int dormantDays,
        CancellationToken cancellationToken)
    {
        dormantDays = Math.Clamp(dormantDays, 1, 3650);
        var cutoff = _clock.UtcNow.AddDays(-dormantDays);
        var users = await _db.Users.AsNoTracking()
            .Where(x => x.Status == IdentityStatus.Active)
            .ToListAsync(cancellationToken);
        return users
            .Where(x => x.LastLoginAt is null || x.LastLoginAt <= cutoff)
            .Select(x => (object)new
            {
                x.Id,
                x.DisplayName,
                x.Email,
                x.Department,
                x.LastLoginAt,
                dormantDays
            })
            .ToArray();
    }

    public async Task<IReadOnlyList<object>> GetOrphanAccountsReportAsync(
        CancellationToken cancellationToken) =>
        (await _db.ProvisioningAccounts.AsNoTracking()
            .Where(x => x.IsOrphan)
            .OrderBy(x => x.ConnectorKey)
            .ThenBy(x => x.UserName)
            .ToListAsync(cancellationToken))
        .Select(x => (object)new
        {
            x.Id,
            x.ConnectorKey,
            x.ExternalId,
            x.UserId,
            x.UserName,
            x.Enabled,
            x.LastSynchronizedAt
        })
        .ToArray();

    public async Task<IReadOnlyList<AuditRecord>> GetAuditAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        return await _db.AuditRecords.AsNoTracking()
            .OrderByDescending(x => x.Sequence)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
    }

    private static CampaignProgress CalculateProgress(
        Guid campaignId,
        IReadOnlyCollection<CertificationItem> items)
    {
        var total = items.Count;
        var pending = items.Count(x => x.Decision == CertificationDecision.Pending);
        var approved = items.Count(x => x.Decision == CertificationDecision.Approved);
        var revoked = items.Count(x => x.Decision == CertificationDecision.Revoked);
        var autoRevoked = items.Count(x => x.Decision == CertificationDecision.AutoRevoked);
        return new CampaignProgress(
            campaignId,
            total,
            pending,
            approved,
            revoked,
            autoRevoked,
            total == 0 ? 100 : Math.Round((total - pending) * 100m / total, 2));
    }

    private async Task ApplyCertificationRevocationAsync(
        CertificationItem item,
        string reason,
        CancellationToken cancellationToken)
    {
        var exclusion = await _db.UserEntitlementExclusions.SingleOrDefaultAsync(
            x => x.UserId == item.UserId && x.EntitlementId == item.EntitlementId,
            cancellationToken);
        if (exclusion is null)
        {
            _db.UserEntitlementExclusions.Add(new UserEntitlementExclusion
            {
                UserId = item.UserId,
                EntitlementId = item.EntitlementId,
                CampaignItemId = item.Id,
                CreatedAt = _clock.UtcNow,
                Reason = reason
            });
        }

        var grants = await _db.UserEntitlementGrants
            .Where(x => x.UserId == item.UserId && x.EntitlementId == item.EntitlementId && x.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var grant in grants)
        {
            grant.RevokedAt = _clock.UtcNow;
        }
    }

    private async Task<ProvisioningAccount> UpsertProvisioningAccountAsync(
        Guid applicationId,
        string connectorKey,
        ConnectorAccountSnapshot snapshot,
        CancellationToken cancellationToken,
        bool isOrphan = false,
        bool synchronizePermissions = true)
    {
        var account = await _db.ProvisioningAccounts.SingleOrDefaultAsync(
            x => x.ConnectorKey == connectorKey && x.ExternalId == snapshot.ExternalId,
            cancellationToken);
        if (account is null)
        {
            account = new ProvisioningAccount
            {
                ConnectorKey = connectorKey,
                ApplicationId = applicationId,
                ExternalId = snapshot.ExternalId
            };
            _db.ProvisioningAccounts.Add(account);
        }

        account.UserId = snapshot.UserId;
        account.UserName = snapshot.UserName;
        account.Enabled = snapshot.Enabled;
        account.IsOrphan = isOrphan;
        account.LastSynchronizedAt = _clock.UtcNow;

        if (synchronizePermissions)
        {
            var oldGrants = await _db.ProvisioningGrants
                .Where(x => x.ProvisioningAccountId == account.Id)
                .ToListAsync(cancellationToken);
            _db.ProvisioningGrants.RemoveRange(oldGrants);
            foreach (var permission in snapshot.Permissions.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var entitlement = await _db.Entitlements.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Permission == permission, cancellationToken);
                _db.ProvisioningGrants.Add(new ProvisioningGrant
                {
                    ProvisioningAccountId = account.Id,
                    EntitlementId = entitlement?.Id,
                    Permission = permission,
                    IsRogue = false
                });
            }
        }

        return account;
    }

    private async Task<IReadOnlyList<string>> GetExpectedPermissionsForApplicationAsync(
        Guid userId,
        Guid applicationId,
        CancellationToken cancellationToken)
    {
        var access = await ResolveDerivationsAsync(userId, cancellationToken);
        var ids = access.Select(x => x.EntitlementId).Distinct().ToArray();
        return await _db.Entitlements.AsNoTracking()
            .Where(x => x.ApplicationId == applicationId && ids.Contains(x.Id))
            .Select(x => x.Permission)
            .ToListAsync(cancellationToken);
    }
}
