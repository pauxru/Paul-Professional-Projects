using JobScheduler.Api.Contracts;
using JobScheduler.Application.Abstractions;
using JobScheduler.Domain.Entities;

namespace JobScheduler.Api;

/// <summary>Maps domain entities to API DTOs (no domain types leak across the HTTP boundary).</summary>
public static class Mapping
{
    public static JobDefinitionDto ToDto(this JobDefinition d) => new(
        d.Id, d.Name, d.HandlerType, d.PayloadJson, d.Queue, d.Priority, d.ConcurrencyLimit, d.Singleton,
        d.Owner, d.Tags, d.DependsOn, d.Enabled, d.RetryStrategy, d.MaxAttempts, d.TimeoutSeconds, d.TriggerType,
        d.CronExpression, d.IntervalSeconds, d.RunAt, d.TimeZoneId, d.MisfirePolicy, d.LastFireAt, d.CreatedAt, d.UpdatedAt);

    public static JobRunDto ToDto(this JobRun r) => new(
        r.Id, r.JobDefinitionId, r.JobName, r.HandlerType, r.Queue, r.Priority, r.State, r.AttemptCount, r.MaxAttempts,
        r.ScheduledAt, r.StartedAt, r.FinishedAt, r.LeaseOwner, r.FencingToken, r.IdempotencyKey, r.CorrelationId,
        r.TriggerKind, r.CancelRequested, r.Output, r.Error);

    public static RunLogDto ToDto(this RunLog l) => new(l.Id, l.Timestamp, l.Level, l.Message, l.NodeId, l.Attempt);

    public static WorkerNodeDto ToDto(this WorkerNode n, DateTimeOffset now) => new(
        n.NodeId, n.Hostname, n.Status.ToString(), n.Tags, n.MaxConcurrency, n.RegisteredAt, n.LastHeartbeat,
        Math.Max(0, (now - n.LastHeartbeat).TotalSeconds));

    public static DeadLetterDto ToDto(this DeadLetterEntry e) => new(
        e.Id, e.JobRunId, e.JobDefinitionId, e.JobName, e.Reason, e.Error, e.AttemptCount, e.DeadLetteredAt,
        e.Replayed, e.ReplayedRunId, e.ReplayedAt);

    public static LeaderDto ToDto(this LeaderView v) => new(v.Owner, v.FencingToken, v.AcquiredAt, v.ExpiresAt, v.IsHeld);

    public static PagedResponse<TOut> ToResponse<TIn, TOut>(this PagedResult<TIn> page, Func<TIn, TOut> map) =>
        new(page.Items.Select(map).ToList(), page.Page, page.PageSize, page.TotalCount, page.TotalPages);
}
