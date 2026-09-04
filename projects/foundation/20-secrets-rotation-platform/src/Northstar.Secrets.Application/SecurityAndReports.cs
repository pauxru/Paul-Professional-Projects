using Northstar.Secrets.Domain;

namespace Northstar.Secrets.Application;

public sealed class FourEyesService(ISecretsRepository repository, IClock clock)
{
    public async Task<ApprovalRequest> RequestAsync(
        ApprovalOperation operation,
        string resource,
        string requestedBy,
        string reason,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var approval = new ApprovalRequest(
            Guid.NewGuid(),
            operation,
            resource,
            requestedBy,
            reason,
            now,
            now.AddMinutes(30));
        await repository.AddApprovalAsync(approval, cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
        return approval;
    }

    public async Task<ApprovalRequest> ApproveAsync(
        Guid approvalId,
        string approver,
        CancellationToken cancellationToken)
    {
        var approval = await GetRequiredAsync(approvalId, cancellationToken);
        approval.Approve(approver, clock.UtcNow);
        await repository.SaveChangesAsync(cancellationToken);
        return approval;
    }

    public async Task<ApprovalRequest> ConsumeAsync(
        Guid approvalId,
        ApprovalOperation expectedOperation,
        string expectedResource,
        string executor,
        CancellationToken cancellationToken)
    {
        var approval = await GetRequiredAsync(approvalId, cancellationToken);
        if (approval.Operation != expectedOperation ||
            !string.Equals(approval.Resource, expectedResource, StringComparison.OrdinalIgnoreCase))
        {
            throw new ForbiddenOperationException("The approval does not authorize this operation.");
        }

        if (!approval.IsApproved)
        {
            throw new ForbiddenOperationException("A second actor must approve this operation.");
        }

        approval.MarkExecuted(clock.UtcNow);
        await repository.SaveChangesAsync(cancellationToken);
        return approval;
    }

    private async Task<ApprovalRequest> GetRequiredAsync(
        Guid approvalId,
        CancellationToken cancellationToken) =>
        await repository.GetApprovalAsync(approvalId, cancellationToken)
        ?? throw new ResourceNotFoundException($"Approval '{approvalId}' was not found.");
}

public sealed class BreakGlassService(
    ISecretsRepository repository,
    SecretLifecycleService lifecycle,
    FourEyesService approvals,
    RotationEngine rotationEngine,
    IClock clock)
{
    public Task<ApprovalRequest> RequestAsync(
        ApprovalOperation operation,
        string resource,
        string actor,
        string reason,
        CancellationToken cancellationToken) =>
        approvals.RequestAsync(operation, resource, actor, reason, cancellationToken);

    public Task<ApprovalRequest> ApproveAsync(
        Guid approvalId,
        string approver,
        CancellationToken cancellationToken) =>
        approvals.ApproveAsync(approvalId, approver, cancellationToken);

    public async Task<SecretValueResult> ReadAsync(
        Guid approvalId,
        string secretName,
        int? version,
        AccessContext access,
        CancellationToken cancellationToken)
    {
        await approvals.ConsumeAsync(
            approvalId,
            ApprovalOperation.BreakGlassRead,
            SecretRecord.NormalizeName(secretName),
            access.Actor,
            cancellationToken);
        var result = await lifecycle.ReadValueWithApprovedBreakGlassAsync(
            secretName, version, access, cancellationToken);
        await repository.AddAuditAsync(
            new AuditRecord(
                Guid.NewGuid(),
                null,
                access.Actor,
                "break-glass.read",
                SecretRecord.NormalizeName(secretName),
                access.Reason,
                access.CorrelationId,
                clock.UtcNow,
                "allowed",
                access.SourceAddress,
                access.UserAgent),
            cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task EmergencyRevokeAsync(
        Guid approvalId,
        string secretName,
        AccessContext access,
        CancellationToken cancellationToken)
    {
        await approvals.ConsumeAsync(
            approvalId,
            ApprovalOperation.EmergencyRevoke,
            SecretRecord.NormalizeName(secretName),
            access.Actor,
            cancellationToken);
        await lifecycle.EmergencyRevokeAsync(secretName, access, cancellationToken);
    }

    public async Task DestroyVersionAsync(
        Guid approvalId,
        string secretName,
        int version,
        AccessContext access,
        CancellationToken cancellationToken)
    {
        var resource = $"{SecretRecord.NormalizeName(secretName)}#v{version}";
        await approvals.ConsumeAsync(
            approvalId,
            ApprovalOperation.DestroyVersion,
            resource,
            access.Actor,
            cancellationToken);
        await lifecycle.DestroyVersionAsync(secretName, version, access, cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> ForceRotateApplicationAsync(
        Guid approvalId,
        string application,
        AccessContext access,
        CancellationToken cancellationToken)
    {
        await approvals.ConsumeAsync(
            approvalId,
            ApprovalOperation.IncidentRotation,
            application.ToLowerInvariant(),
            access.Actor,
            cancellationToken);

        var secrets = (await repository.ListSecretsAsync(cancellationToken))
            .Where(x => x.Name.StartsWith(application.ToLowerInvariant() + "/", StringComparison.Ordinal))
            .ToArray();
        var rotationIds = new List<Guid>();
        foreach (var secret in secrets)
        {
            var rotation = await rotationEngine.RequestAsync(
                new RequestRotationCommand(
                    secret.Name,
                    RotationStrategyKind.DualWrite,
                    $"incident:{approvalId}:{secret.Id}",
                    access.Actor,
                    access.CorrelationId),
                cancellationToken);
            rotationIds.Add(rotation.Id);
        }

        await repository.AddAuditAsync(
            new AuditRecord(
                Guid.NewGuid(),
                null,
                access.Actor,
                "incident.force-rotate",
                application,
                access.Reason,
                access.CorrelationId,
                clock.UtcNow,
                "allowed",
                access.SourceAddress,
                access.UserAgent),
            cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
        return rotationIds;
    }
}

public sealed record ExpiryReportItem(
    string Name,
    int Version,
    DateTimeOffset ExpiresAt,
    TimeSpan Remaining,
    SecretCriticality Criticality,
    bool Overdue);

public sealed record AccessReportItem(
    string Actor,
    string Action,
    string Reason,
    string CorrelationId,
    DateTimeOffset OccurredAt,
    string Outcome,
    string? SourceAddress);

public sealed record AccessAnomaly(
    string Type,
    string Actor,
    string Resource,
    DateTimeOffset DetectedAt,
    string Detail,
    string Severity);

public sealed class ReportingService(ISecretsRepository repository, IClock clock)
{
    public async Task<IReadOnlyList<ExpiryReportItem>> UpcomingExpiryAsync(
        TimeSpan horizon,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var cutoff = now.Add(horizon);
        var secrets = await repository.ListSecretsAsync(cancellationToken);
        return secrets
            .Where(x => x.CurrentVersion is not null && x.CurrentVersion.ExpiresAt <= cutoff)
            .Select(x => new ExpiryReportItem(
                x.Name,
                x.CurrentVersion!.VersionNumber,
                x.CurrentVersion.ExpiresAt,
                x.CurrentVersion.ExpiresAt - now,
                x.Criticality,
                x.CurrentVersion.ExpiresAt <= now))
            .OrderBy(x => x.ExpiresAt)
            .ToArray();
    }

    public async Task<IReadOnlyList<AccessReportItem>> AccessReportAsync(
        string secretName,
        DateTimeOffset? since,
        CancellationToken cancellationToken)
    {
        var secret = await repository.GetSecretAsync(
            SecretRecord.NormalizeName(secretName), cancellationToken)
            ?? throw new ResourceNotFoundException($"Secret '{secretName}' was not found.");
        var records = await repository.ListAuditsAsync(secret.Id, since, cancellationToken);
        return records
            .Select(x => new AccessReportItem(
                x.Actor, x.Action, x.Reason, x.CorrelationId, x.OccurredAt, x.Outcome, x.SourceAddress))
            .OrderByDescending(x => x.OccurredAt)
            .ToArray();
    }

    public async Task<IReadOnlyList<AccessAnomaly>> AccessAnomaliesAsync(
        DateTimeOffset? since,
        CancellationToken cancellationToken)
    {
        var records = (await repository.ListAuditsAsync(null, null, cancellationToken))
            .Where(x => x.Action == "secret.read-value" && x.Outcome == "allowed")
            .OrderBy(x => x.OccurredAt)
            .ToArray();
        var windowStart = since ?? clock.UtcNow.AddDays(-30);
        var anomalies = new List<AccessAnomaly>();

        foreach (var group in records.GroupBy(x => new { x.Actor, x.Resource }))
        {
            var ordered = group.OrderBy(x => x.OccurredAt).ToArray();
            var first = ordered[0];
            if (first.OccurredAt >= windowStart)
            {
                anomalies.Add(new AccessAnomaly(
                    "FirstTimeReader",
                    first.Actor,
                    first.Resource,
                    first.OccurredAt,
                    "Actor read this secret for the first recorded time.",
                    "medium"));
                anomalies.Add(new AccessAnomaly(
                    "UnusualReader",
                    first.Actor,
                    first.Resource,
                    first.OccurredAt,
                    "The reader has no prior baseline for this secret.",
                    "medium"));
            }

            foreach (var record in ordered.Where(x =>
                         x.OccurredAt >= windowStart &&
                         (x.OccurredAt.Hour < 8 || x.OccurredAt.Hour >= 18)))
            {
                anomalies.Add(new AccessAnomaly(
                    "OutsideBusinessHours",
                    record.Actor,
                    record.Resource,
                    record.OccurredAt,
                    "Read occurred outside the configured 08:00-18:00 UTC window.",
                    "high"));
            }

            foreach (var spike in ordered.Where(x => x.OccurredAt >= windowStart)
                         .GroupBy(x => new
                         {
                             x.OccurredAt.Year,
                             x.OccurredAt.Month,
                             x.OccurredAt.Day,
                             x.OccurredAt.Hour
                         })
                         .Where(x => x.Count() >= 5))
            {
                anomalies.Add(new AccessAnomaly(
                    "ReadSpike",
                    first.Actor,
                    first.Resource,
                    spike.Max(x => x.OccurredAt),
                    $"{spike.Count()} reads occurred within one UTC hour.",
                    "high"));
            }
        }

        return anomalies.OrderByDescending(x => x.DetectedAt).ToArray();
    }
}
