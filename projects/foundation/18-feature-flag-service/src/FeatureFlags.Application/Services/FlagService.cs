using System.Text.Json;
using System.Text.Json.Nodes;
using FeatureFlags.Domain;

namespace FeatureFlags.Application;

public sealed record PromotionDiff(string Path, string Change, string? Source, string? Target);
public sealed record PromotionPreview(string SourceEnvironment, string TargetEnvironment, IReadOnlyList<PromotionDiff> Differences);
public sealed record StaleFlag(string FlagKey, FlagLifecycleStatus LifecycleStatus, DateTimeOffset? LastEvaluatedAt, DateTimeOffset? RemovalDueAt, string Reason);

public sealed class FlagService(
    IProjectEnvironmentStore environments,
    IAuditStore audits,
    IApprovalStore approvals,
    IAnalyticsStore analytics,
    IConfigurationBroadcaster broadcaster,
    IClock clock)
{
    private readonly FlagEvaluator _evaluator = new();

    public Task<IReadOnlyList<ProjectInfo>> ListProjectsAsync(CancellationToken cancellationToken) => environments.ListProjectsAsync(cancellationToken);

    public async Task<ProjectInfo> CreateProjectAsync(string key, string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(name))
        {
            throw new DomainValidationException(["Project key and name are required."]);
        }

        return await environments.CreateProjectAsync(key.Trim(), name.Trim(), clock.UtcNow, cancellationToken);
    }

    public async Task<StoredEnvironment> CreateEnvironmentAsync(
        string projectKey,
        string environmentKey,
        string name,
        string serverSdkKey,
        string clientSdkKey,
        CancellationToken cancellationToken)
    {
        var project = await environments.FindProjectAsync(projectKey, cancellationToken) ?? throw new NotFoundException("Project");
        if (string.IsNullOrWhiteSpace(environmentKey) || string.IsNullOrWhiteSpace(serverSdkKey) || string.IsNullOrWhiteSpace(clientSdkKey))
        {
            throw new DomainValidationException(["Environment key, server SDK key, and client SDK key are required."]);
        }

        var now = clock.UtcNow;
        var configuration = new EnvironmentConfiguration
        {
            ProjectKey = project.Key,
            EnvironmentKey = environmentKey.Trim(),
            Version = 1,
            GeneratedAt = now
        };
        return await environments.CreateEnvironmentAsync(project, environmentKey.Trim(), name.Trim(), serverSdkKey, clientSdkKey, configuration, now, cancellationToken);
    }

    public Task<IReadOnlyList<EnvironmentInfo>> ListEnvironmentsAsync(string projectKey, CancellationToken cancellationToken) => environments.ListEnvironmentsAsync(projectKey, cancellationToken);

    public async Task<StoredEnvironment> GetEnvironmentAsync(string projectKey, string environmentKey, CancellationToken cancellationToken) =>
        await environments.FindEnvironmentAsync(projectKey, environmentKey, cancellationToken) ?? throw new NotFoundException("Environment");

    public async Task<EnvironmentConfiguration> GetPublicConfigurationAsync(string projectKey, string environmentKey, string sdkKey, CancellationToken cancellationToken)
    {
        var stored = await GetEnvironmentAsync(projectKey, environmentKey, cancellationToken);
        if (string.Equals(sdkKey, stored.Environment.ServerSdkKey, StringComparison.Ordinal))
        {
            return stored.Configuration;
        }

        if (string.Equals(sdkKey, stored.Environment.ClientSdkKey, StringComparison.Ordinal))
        {
            return stored.Configuration.ForClient();
        }

        throw new UnauthorizedAccessException("The SDK key is invalid for this environment.");
    }

    public async Task<FlagDefinition> SaveFlagAsync(
        string projectKey,
        string environmentKey,
        FlagDefinition flag,
        ChangeContext change,
        bool bypassProductionApproval,
        CancellationToken cancellationToken)
    {
        var stored = await GetEnvironmentAsync(projectKey, environmentKey, cancellationToken);
        if (IsProduction(stored) && !bypassProductionApproval)
        {
            throw new ApprovalRequiredException();
        }

        var flags = stored.Configuration.Flags.Where(existing => !string.Equals(existing.Key, flag.Key, StringComparison.Ordinal)).Append(flag).ToArray();
        var after = PrepareConfiguration(stored.Configuration, flags, stored.Configuration.Segments);
        await SaveConfigurationWithAuditAsync(stored, after, change, "FlagSaved", $"flags/{flag.Key}", cancellationToken);
        return flag;
    }

    public async Task<FlagDefinition> SetKillSwitchAsync(
        string projectKey,
        string environmentKey,
        string flagKey,
        bool enabled,
        ChangeContext change,
        CancellationToken cancellationToken)
    {
        var stored = await GetEnvironmentAsync(projectKey, environmentKey, cancellationToken);
        var existing = stored.Configuration.FindFlag(flagKey) ?? throw new NotFoundException("Flag");
        var updated = existing with { IsOn = enabled };
        var flags = stored.Configuration.Flags.Where(flag => !string.Equals(flag.Key, flagKey, StringComparison.Ordinal)).Append(updated).ToArray();
        var after = PrepareConfiguration(stored.Configuration, flags, stored.Configuration.Segments);
        await SaveConfigurationWithAuditAsync(stored, after, change, enabled ? "KillSwitchEnabled" : "KillSwitchBypass", $"flags/{flagKey}", cancellationToken);
        return updated;
    }

    public async Task<EvaluationResult> EvaluateAsync(
        string projectKey,
        string environmentKey,
        string flagKey,
        EvaluationContext context,
        CancellationToken cancellationToken)
    {
        var stored = await GetEnvironmentAsync(projectKey, environmentKey, cancellationToken);
        var result = _evaluator.Evaluate(stored.Configuration, flagKey, context, clock.UtcNow);
        await analytics.RecordEvaluationAsync(new AnalyticsEvent(
            projectKey, environmentKey, "evaluation", flagKey, result.VariationIndex, context.Key, null, null, clock.UtcNow), cancellationToken);
        return result;
    }

    public async Task<PromotionPreview> PreviewPromotionAsync(string projectKey, string sourceEnvironment, string targetEnvironment, CancellationToken cancellationToken)
    {
        var source = await GetEnvironmentAsync(projectKey, sourceEnvironment, cancellationToken);
        var target = await GetEnvironmentAsync(projectKey, targetEnvironment, cancellationToken);
        return new PromotionPreview(sourceEnvironment, targetEnvironment, JsonDiff.Diff(source.Configuration, target.Configuration));
    }

    public async Task PromoteAsync(string projectKey, string sourceEnvironment, string targetEnvironment, ChangeContext change, bool bypassProductionApproval, CancellationToken cancellationToken)
    {
        var source = await GetEnvironmentAsync(projectKey, sourceEnvironment, cancellationToken);
        var target = await GetEnvironmentAsync(projectKey, targetEnvironment, cancellationToken);
        if (IsProduction(target) && !bypassProductionApproval)
        {
            throw new ApprovalRequiredException();
        }

        var after = PrepareConfiguration(target.Configuration, source.Configuration.Flags, source.Configuration.Segments);
        await SaveConfigurationWithAuditAsync(target, after, change, "EnvironmentPromoted", $"environments/{targetEnvironment}", cancellationToken);
    }

    public async Task<ApprovalRequest> RequestPromotionAsync(string projectKey, string sourceEnvironment, string targetEnvironment, ChangeContext change, CancellationToken cancellationToken)
    {
        var source = await GetEnvironmentAsync(projectKey, sourceEnvironment, cancellationToken);
        var target = await GetEnvironmentAsync(projectKey, targetEnvironment, cancellationToken);
        if (!IsProduction(target))
        {
            throw new DomainValidationException(["Promotion approval requests are only required for production targets."]);
        }

        var proposed = PrepareConfiguration(target.Configuration, source.Configuration.Flags, source.Configuration.Segments, incrementVersion: false);
        var request = new ApprovalRequest(
            Guid.NewGuid(), projectKey, targetEnvironment, $"promotions/{sourceEnvironment}-to-{targetEnvironment}",
            JsonSerializer.Serialize(proposed, FeatureFlagJson.Options), change.Actor, change.Comment,
            ApprovalStatus.Pending, null, null, clock.UtcNow, null, null);
        await approvals.AddAsync(request, cancellationToken);
        await AppendAuditAsync(target, target.Configuration, target.Configuration, change, "PromotionApprovalRequested", request.Resource, cancellationToken);
        return request;
    }

    public async Task<IReadOnlyList<AuditRecord>> GetAuditAsync(string projectKey, string environmentKey, CancellationToken cancellationToken) =>
        await audits.ListAsync(projectKey, environmentKey, cancellationToken);

    public async Task RevertAsync(Guid auditId, ChangeContext change, CancellationToken cancellationToken)
    {
        var audit = await audits.FindAsync(auditId, cancellationToken) ?? throw new NotFoundException("Audit entry");
        var stored = await GetEnvironmentAsync(audit.ProjectKey, audit.EnvironmentKey, cancellationToken);
        var prior = JsonSerializer.Deserialize<EnvironmentConfiguration>(audit.BeforeJson, FeatureFlagJson.Options)
            ?? throw new DomainValidationException(["The audit record does not contain a valid prior configuration."]);
        var restored = PrepareConfiguration(stored.Configuration, prior.Flags, prior.Segments);
        await SaveConfigurationWithAuditAsync(stored, restored, change, "ConfigurationReverted", audit.Resource, cancellationToken);
    }

    public async Task<ApprovalRequest> RequestFlagChangeAsync(
        string projectKey,
        string environmentKey,
        FlagDefinition proposedFlag,
        ChangeContext change,
        CancellationToken cancellationToken)
    {
        var stored = await GetEnvironmentAsync(projectKey, environmentKey, cancellationToken);
        if (!IsProduction(stored))
        {
            throw new DomainValidationException(["Approvals are required only for production environments in this demonstration."]);
        }

        var flags = stored.Configuration.Flags.Where(flag => !string.Equals(flag.Key, proposedFlag.Key, StringComparison.Ordinal)).Append(proposedFlag).ToArray();
        var after = PrepareConfiguration(stored.Configuration, flags, stored.Configuration.Segments, incrementVersion: false);
        var request = new ApprovalRequest(
            Guid.NewGuid(), projectKey, environmentKey, $"flags/{proposedFlag.Key}",
            JsonSerializer.Serialize(after, FeatureFlagJson.Options), change.Actor, change.Comment,
            ApprovalStatus.Pending, null, null, clock.UtcNow, null, null);
        await approvals.AddAsync(request, cancellationToken);
        await AppendAuditAsync(stored, stored.Configuration, stored.Configuration, change, "ApprovalRequested", request.Resource, cancellationToken);
        return request;
    }

    public async Task<ApprovalRequest> ReviewApprovalAsync(Guid id, string reviewer, bool approved, string? comment, CancellationToken cancellationToken)
    {
        var request = await approvals.FindAsync(id, cancellationToken) ?? throw new NotFoundException("Approval request");
        if (request.Status != ApprovalStatus.Pending)
        {
            throw new DomainValidationException(["Only pending approval requests may be reviewed."]);
        }

        if (string.Equals(request.RequestedBy, reviewer, StringComparison.Ordinal))
        {
            throw new FourEyesViolationException();
        }

        var updated = request with
        {
            Status = approved ? ApprovalStatus.Approved : ApprovalStatus.Rejected,
            ReviewedBy = reviewer,
            ReviewComment = comment,
            ReviewedAt = clock.UtcNow
        };
        await approvals.UpdateAsync(updated, cancellationToken);
        var stored = await GetEnvironmentAsync(request.ProjectKey, request.EnvironmentKey, cancellationToken);
        await AppendAuditAsync(stored, stored.Configuration, stored.Configuration, new ChangeContext(reviewer, comment), approved ? "ApprovalApproved" : "ApprovalRejected", request.Resource, cancellationToken);
        return updated;
    }

    public async Task ApplyApprovalAsync(Guid id, string actor, CancellationToken cancellationToken)
    {
        var request = await approvals.FindAsync(id, cancellationToken) ?? throw new NotFoundException("Approval request");
        if (request.Status != ApprovalStatus.Approved)
        {
            throw new DomainValidationException(["Only approved requests may be applied."]);
        }

        var stored = await GetEnvironmentAsync(request.ProjectKey, request.EnvironmentKey, cancellationToken);
        var proposed = JsonSerializer.Deserialize<EnvironmentConfiguration>(request.ProposedConfigurationJson, FeatureFlagJson.Options)
            ?? throw new DomainValidationException(["Approval request payload is invalid."]);
        var after = PrepareConfiguration(stored.Configuration, proposed.Flags, proposed.Segments);
        await SaveConfigurationWithAuditAsync(stored, after, new ChangeContext(actor, request.RequestComment), "ApprovalApplied", request.Resource, cancellationToken);
        await approvals.UpdateAsync(request with { Status = ApprovalStatus.Applied, AppliedAt = clock.UtcNow }, cancellationToken);
    }

    public Task<IReadOnlyList<ApprovalRequest>> GetApprovalsAsync(string projectKey, string environmentKey, CancellationToken cancellationToken) => approvals.ListAsync(projectKey, environmentKey, cancellationToken);

    public Task<IReadOnlyList<FlagEvaluationMetric>> GetMetricsAsync(string projectKey, string environmentKey, CancellationToken cancellationToken) => analytics.GetMetricsAsync(projectKey, environmentKey, cancellationToken);

    public Task<IReadOnlyList<AnalyticsEvent>> GetAnalyticsEventsAsync(string projectKey, string environmentKey, CancellationToken cancellationToken) =>
        analytics.GetEventsAsync(projectKey, environmentKey, cancellationToken);

    public async Task<IReadOnlyList<StaleFlag>> GetStaleFlagsAsync(string projectKey, string environmentKey, int staleDays, CancellationToken cancellationToken)
    {
        if (staleDays < 1)
        {
            throw new DomainValidationException(["staleDays must be at least one."]);
        }

        var stored = await GetEnvironmentAsync(projectKey, environmentKey, cancellationToken);
        var cutoff = clock.UtcNow.AddDays(-staleDays);
        var report = new List<StaleFlag>();
        foreach (var flag in stored.Configuration.Flags)
        {
            var last = await analytics.LastEvaluatedAsync(projectKey, environmentKey, flag.Key, cancellationToken);
            if (flag.RemovalDueAt.HasValue && flag.RemovalDueAt < clock.UtcNow)
            {
                report.Add(new StaleFlag(flag.Key, flag.LifecycleStatus, last, flag.RemovalDueAt, "Removal deadline overdue"));
            }
            else if (!last.HasValue || last < cutoff)
            {
                report.Add(new StaleFlag(flag.Key, flag.LifecycleStatus, last, flag.RemovalDueAt, "No recent evaluations"));
            }
        }

        return report;
    }

    public Task RecordEventsAsync(IReadOnlyList<AnalyticsEvent> events, CancellationToken cancellationToken) => analytics.RecordEventsAsync(events, cancellationToken);

    private EnvironmentConfiguration PrepareConfiguration(EnvironmentConfiguration current, IReadOnlyList<FlagDefinition> flags, IReadOnlyList<SegmentDefinition> segments, bool incrementVersion = true)
    {
        var configuration = current with
        {
            Version = incrementVersion ? current.Version + 1 : current.Version,
            GeneratedAt = clock.UtcNow,
            Flags = flags,
            Segments = segments
        };
        var errors = FlagConfigurationValidator.Validate(configuration);
        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }

        return configuration;
    }

    private async Task SaveConfigurationWithAuditAsync(StoredEnvironment stored, EnvironmentConfiguration after, ChangeContext change, string action, string resource, CancellationToken cancellationToken)
    {
        await environments.SaveConfigurationAsync(stored.Environment, after, clock.UtcNow, cancellationToken);
        await AppendAuditAsync(stored, stored.Configuration, after, change, action, resource, cancellationToken);
        await broadcaster.PublishAsync(new ConfigurationChanged(stored.Environment.ProjectKey, stored.Environment.Key, after.Version), cancellationToken);
    }

    private Task AppendAuditAsync(StoredEnvironment stored, EnvironmentConfiguration before, EnvironmentConfiguration after, ChangeContext change, string action, string resource, CancellationToken cancellationToken)
    {
        var beforeJson = JsonSerializer.Serialize(before, FeatureFlagJson.Options);
        var afterJson = JsonSerializer.Serialize(after, FeatureFlagJson.Options);
        var audit = new AuditRecord(Guid.NewGuid(), stored.Environment.ProjectKey, stored.Environment.Key, change.Actor, action, resource,
            beforeJson, afterJson, JsonSerializer.Serialize(JsonDiff.Diff(before, after), FeatureFlagJson.Options), change.Comment,
            change.TicketReference, change.CorrelationId, change.SourceIp, change.UserAgent, clock.UtcNow);
        return audits.AppendAsync(audit, cancellationToken);
    }

    private static bool IsProduction(StoredEnvironment environment) => string.Equals(environment.Environment.Key, "production", StringComparison.OrdinalIgnoreCase);
}

public static class JsonDiff
{
    public static IReadOnlyList<PromotionDiff> Diff(object before, object after)
    {
        var left = JsonNode.Parse(JsonSerializer.Serialize(before, FeatureFlagJson.Options));
        var right = JsonNode.Parse(JsonSerializer.Serialize(after, FeatureFlagJson.Options));
        var differences = new List<PromotionDiff>();
        Compare(left, right, "$", differences);
        return differences;
    }

    private static void Compare(JsonNode? left, JsonNode? right, string path, ICollection<PromotionDiff> differences)
    {
        if (JsonNode.DeepEquals(left, right))
        {
            return;
        }

        if (left is JsonObject leftObject && right is JsonObject rightObject)
        {
            foreach (var property in leftObject.Select(item => item.Key).Union(rightObject.Select(item => item.Key), StringComparer.Ordinal).Order())
            {
                Compare(leftObject[property], rightObject[property], $"{path}.{property}", differences);
            }
            return;
        }

        differences.Add(new PromotionDiff(path, left is null ? "Added" : right is null ? "Removed" : "Changed", left?.ToJsonString(), right?.ToJsonString()));
    }
}
