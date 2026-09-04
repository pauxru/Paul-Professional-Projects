using System.Text.Json;
using FeatureFlags.Domain;

namespace FeatureFlags.Application;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed record ProjectInfo(Guid Id, string Key, string Name, DateTimeOffset CreatedAt);

public sealed record EnvironmentInfo(
    Guid Id,
    Guid ProjectId,
    string ProjectKey,
    string Key,
    string Name,
    string ServerSdkKey,
    string ClientSdkKey,
    long Version,
    DateTimeOffset UpdatedAt);

public sealed record StoredEnvironment(EnvironmentInfo Environment, EnvironmentConfiguration Configuration);

public sealed record ChangeContext(
    string Actor,
    string? Comment = null,
    string? TicketReference = null,
    string? CorrelationId = null,
    string? SourceIp = null,
    string? UserAgent = null);

public sealed record AuditRecord(
    Guid Id,
    string ProjectKey,
    string EnvironmentKey,
    string Actor,
    string Action,
    string Resource,
    string BeforeJson,
    string AfterJson,
    string DiffJson,
    string? Comment,
    string? TicketReference,
    string? CorrelationId,
    string? SourceIp,
    string? UserAgent,
    DateTimeOffset OccurredAt);

public enum ApprovalStatus { Pending, Approved, Rejected, Applied }

public sealed record ApprovalRequest(
    Guid Id,
    string ProjectKey,
    string EnvironmentKey,
    string Resource,
    string ProposedConfigurationJson,
    string RequestedBy,
    string? RequestComment,
    ApprovalStatus Status,
    string? ReviewedBy,
    string? ReviewComment,
    DateTimeOffset RequestedAt,
    DateTimeOffset? ReviewedAt,
    DateTimeOffset? AppliedAt);

public sealed record FlagEvaluationMetric(
    string ProjectKey,
    string EnvironmentKey,
    string FlagKey,
    int? VariationIndex,
    long Count,
    DateTimeOffset LastEvaluatedAt,
    long UniqueContextCount);

public sealed record AnalyticsEvent(
    string ProjectKey,
    string EnvironmentKey,
    string Kind,
    string? FlagKey,
    int? VariationIndex,
    string ContextKey,
    string? MetricKey,
    decimal? NumericValue,
    DateTimeOffset OccurredAt);

public sealed record ConfigurationChanged(string ProjectKey, string EnvironmentKey, long Version);

public interface IProjectEnvironmentStore
{
    Task<IReadOnlyList<ProjectInfo>> ListProjectsAsync(CancellationToken cancellationToken);
    Task<ProjectInfo?> FindProjectAsync(string projectKey, CancellationToken cancellationToken);
    Task<ProjectInfo> CreateProjectAsync(string key, string name, DateTimeOffset now, CancellationToken cancellationToken);
    Task<StoredEnvironment?> FindEnvironmentAsync(string projectKey, string environmentKey, CancellationToken cancellationToken);
    Task<IReadOnlyList<EnvironmentInfo>> ListEnvironmentsAsync(string projectKey, CancellationToken cancellationToken);
    Task<StoredEnvironment> CreateEnvironmentAsync(ProjectInfo project, string key, string name, string serverSdkKey, string clientSdkKey, EnvironmentConfiguration configuration, DateTimeOffset now, CancellationToken cancellationToken);
    Task SaveConfigurationAsync(EnvironmentInfo environment, EnvironmentConfiguration configuration, DateTimeOffset now, CancellationToken cancellationToken);
}

public interface IAuditStore
{
    Task AppendAsync(AuditRecord record, CancellationToken cancellationToken);
    Task<IReadOnlyList<AuditRecord>> ListAsync(string projectKey, string environmentKey, CancellationToken cancellationToken);
    Task<AuditRecord?> FindAsync(Guid id, CancellationToken cancellationToken);
}

public interface IApprovalStore
{
    Task AddAsync(ApprovalRequest request, CancellationToken cancellationToken);
    Task<ApprovalRequest?> FindAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<ApprovalRequest>> ListAsync(string projectKey, string environmentKey, CancellationToken cancellationToken);
    Task UpdateAsync(ApprovalRequest request, CancellationToken cancellationToken);
}

public interface IAnalyticsStore
{
    Task RecordEvaluationAsync(AnalyticsEvent evaluation, CancellationToken cancellationToken);
    Task RecordEventsAsync(IReadOnlyList<AnalyticsEvent> events, CancellationToken cancellationToken);
    Task<IReadOnlyList<FlagEvaluationMetric>> GetMetricsAsync(string projectKey, string environmentKey, CancellationToken cancellationToken);
    Task<DateTimeOffset?> LastEvaluatedAsync(string projectKey, string environmentKey, string flagKey, CancellationToken cancellationToken);
    Task<IReadOnlyList<AnalyticsEvent>> GetEventsAsync(string projectKey, string environmentKey, CancellationToken cancellationToken);
}

public interface IConfigurationBroadcaster
{
    ValueTask PublishAsync(ConfigurationChanged change, CancellationToken cancellationToken);
    IAsyncEnumerable<ConfigurationChanged> SubscribeAsync(string projectKey, string environmentKey, CancellationToken cancellationToken);
}

public sealed class DomainValidationException(IReadOnlyList<string> errors) : Exception(string.Join(" ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public sealed class NotFoundException(string resource) : Exception($"{resource} was not found.");
public sealed class ApprovalRequiredException() : Exception("Production changes require an approved request.");
public sealed class FourEyesViolationException() : Exception("A requester cannot review their own production change.");
