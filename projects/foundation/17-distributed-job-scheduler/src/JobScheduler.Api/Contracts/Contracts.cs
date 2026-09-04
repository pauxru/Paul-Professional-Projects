using JobScheduler.Domain;

namespace JobScheduler.Api.Contracts;

public sealed record TokenRequest(string? Subject, string[]? Scopes);
public sealed record TokenResponse(string AccessToken, string TokenType, DateTimeOffset ExpiresAt, string[] Scopes);

public sealed record CreateJobDefinitionRequest(
    string Name,
    string HandlerType,
    string? PayloadJson,
    string? Queue,
    int? Priority,
    int? ConcurrencyLimit,
    bool? Singleton,
    string? Owner,
    string[]? Tags,
    string[]? DependsOn,
    RetryStrategy? RetryStrategy,
    double? RetryBaseSeconds,
    double? RetryMaxSeconds,
    double? RetryJitter,
    int? MaxAttempts,
    int? TimeoutSeconds,
    int? DeadlineSeconds,
    TriggerType? TriggerType,
    string? CronExpression,
    int? IntervalSeconds,
    DateTimeOffset? RunAt,
    string? TimeZoneId,
    MisfirePolicy? MisfirePolicy,
    int? CatchUpWindowSeconds,
    int? MaxCatchUp);

public sealed record UpdateJobDefinitionRequest(
    string? PayloadJson,
    int? Priority,
    int? ConcurrencyLimit,
    bool? Singleton,
    int? MaxAttempts,
    int? TimeoutSeconds);

public sealed record TriggerJobRequest(string? PayloadJson, string? IdempotencyKey, string? CorrelationId);

public sealed record JobDefinitionDto(
    Guid Id, string Name, string HandlerType, string PayloadJson, string Queue, int Priority,
    int ConcurrencyLimit, bool Singleton, string Owner, IReadOnlyList<string> Tags, IReadOnlyList<string> DependsOn,
    bool Enabled, RetryStrategy RetryStrategy, int MaxAttempts, int TimeoutSeconds, TriggerType TriggerType,
    string? CronExpression, int? IntervalSeconds, DateTimeOffset? RunAt, string TimeZoneId, MisfirePolicy MisfirePolicy,
    DateTimeOffset? LastFireAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record JobRunDto(
    Guid Id, Guid JobDefinitionId, string JobName, string HandlerType, string Queue, int Priority,
    RunState State, int AttemptCount, int MaxAttempts, DateTimeOffset ScheduledAt, DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt, string? LeaseOwner, long FencingToken, string IdempotencyKey, string CorrelationId,
    string TriggerKind, bool CancelRequested, string? Output, string? Error);

public sealed record RunLogDto(long Id, DateTimeOffset Timestamp, string Level, string Message, string? NodeId, int Attempt);

public sealed record WorkerNodeDto(
    string NodeId, string Hostname, string Status, IReadOnlyList<string> Tags, int MaxConcurrency,
    DateTimeOffset RegisteredAt, DateTimeOffset LastHeartbeat, double HeartbeatAgeSeconds);

public sealed record DeadLetterDto(
    Guid Id, Guid JobRunId, Guid JobDefinitionId, string JobName, string Reason, string? Error,
    int AttemptCount, DateTimeOffset DeadLetteredAt, bool Replayed, Guid? ReplayedRunId, DateTimeOffset? ReplayedAt);

public sealed record LeaderDto(string? Owner, long FencingToken, DateTimeOffset? AcquiredAt, DateTimeOffset ExpiresAt, bool IsHeld);

public sealed record UpcomingOccurrenceDto(
    Guid JobDefinitionId, string JobName, TriggerType TriggerType, string TimeZoneId, DateTimeOffset NextFireUtc, DateTimeOffset NextFireLocal);

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount, int TotalPages);
