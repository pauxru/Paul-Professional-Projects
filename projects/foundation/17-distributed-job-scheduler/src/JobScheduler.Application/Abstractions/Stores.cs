using JobScheduler.Domain;
using JobScheduler.Domain.Entities;

namespace JobScheduler.Application.Abstractions;

/// <summary>Filter for listing runs.</summary>
public sealed record RunQuery(
    Guid? JobDefinitionId = null,
    RunState? State = null,
    string? Queue = null,
    string? CorrelationId = null,
    string? Search = null);

/// <summary>Outcome of an attempt to atomically claim a run.</summary>
public sealed record ClaimResult(bool Claimed, JobRun? Run, long FencingToken)
{
    public static readonly ClaimResult Lost = new(false, null, 0);
}

/// <summary>Result of a heartbeat: whether the lease is still held and whether cancel was requested.</summary>
public sealed record HeartbeatOutcome(bool LeaseHeld, bool CancelRequested)
{
    public static readonly HeartbeatOutcome Lost = new(false, false);
}

public interface IJobDefinitionStore
{
    Task<JobDefinition?> GetAsync(Guid id, CancellationToken ct);
    Task<JobDefinition?> GetByNameAsync(string name, CancellationToken ct);
    Task<PagedResult<JobDefinition>> ListAsync(PageRequest page, bool? enabledOnly, CancellationToken ct);
    Task<IReadOnlyList<JobDefinition>> ListEnabledScheduledAsync(CancellationToken ct);
    Task<IReadOnlyList<JobDefinition>> ListAllAsync(CancellationToken ct);
    Task AddAsync(JobDefinition definition, CancellationToken ct);
    Task UpdateAsync(JobDefinition definition, CancellationToken ct);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

public interface IJobRunStore
{
    Task<JobRun?> GetAsync(Guid id, CancellationToken ct);
    Task AddAsync(JobRun run, CancellationToken ct);
    Task AddRangeAsync(IEnumerable<JobRun> runs, CancellationToken ct);
    Task<PagedResult<JobRun>> ListAsync(RunQuery query, PageRequest page, CancellationToken ct);

    /// <summary>Pending runs whose scheduled time has arrived, ordered by priority with aging.</summary>
    Task<IReadOnlyList<JobRun>> GetDueAsync(DateTimeOffset now, double agingBoostPerMinute, int limit, CancellationToken ct);

    /// <summary>
    /// Atomically claims a single run: conditional update guarded by state == Pending. A fresh,
    /// strictly-greater fencing token is minted inside the same transaction. When
    /// <paramref name="singleton"/> is set, the update also requires that no sibling run of the same
    /// definition is currently active, so a singleton definition can never have two live instances.
    /// Returns whether the caller won the race.
    /// </summary>
    Task<ClaimResult> TryClaimAsync(Guid runId, string nodeId, Guid leaseToken, DateTimeOffset now, TimeSpan leaseDuration, bool singleton, CancellationToken ct);

    /// <summary>Transitions a claimed run to Running (guarded by lease token). Returns false if lost.</summary>
    Task<bool> TryStartAsync(Guid runId, Guid leaseToken, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Extends the lease if the token still owns it and reports whether a cancel has been requested.
    /// <see cref="HeartbeatOutcome.LeaseHeld"/> false means the lease was lost (reclaimed).
    /// </summary>
    Task<HeartbeatOutcome> HeartbeatAsync(Guid runId, Guid leaseToken, DateTimeOffset newExpiry, CancellationToken ct);

    /// <summary>
    /// Requests cooperative cancellation of a run. A Pending run is cancelled immediately; an active
    /// run has its cancel flag set so the executing worker can observe it and stop.
    /// </summary>
    Task<bool> RequestCancelAsync(Guid runId, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Writes back a terminal/failed result, guarded by the fencing token. A stale worker whose
    /// token no longer matches is rejected (returns false) — this is the fencing safety property.
    /// </summary>
    Task<bool> TryCompleteAsync(Guid runId, long fencingToken, RunState finalState, string? output, string? error, DateTimeOffset now, CancellationToken ct);

    /// <summary>Reclaims active runs whose lease has expired back to Pending. Returns the count reclaimed.</summary>
    Task<int> ReclaimExpiredLeasesAsync(DateTimeOffset now, CancellationToken ct);

    /// <summary>Re-arms a Failed/TimedOut run as Pending at the retry instant (Retrying -&gt; Pending).</summary>
    Task ScheduleRetryAsync(Guid runId, DateTimeOffset nextScheduledAt, CancellationToken ct);

    /// <summary>Moves a Failed/TimedOut run to DeadLettered (poison / exhausted budget).</summary>
    Task MarkDeadLetteredAsync(Guid runId, CancellationToken ct);

    /// <summary>Replay: re-arms a dead-lettered run as a fresh Pending instance. Returns the run.</summary>
    Task<JobRun?> ReplayResetAsync(Guid runId, DateTimeOffset scheduledAt, CancellationToken ct);

    Task<int> CountActiveGlobalAsync(CancellationToken ct);
    Task<int> CountActiveByDefinitionAsync(Guid jobDefinitionId, CancellationToken ct);
    Task<int> CountActiveByQueueAsync(string queue, CancellationToken ct);
    Task<bool> HasActiveForDefinitionAsync(Guid jobDefinitionId, CancellationToken ct);
    Task<bool> ExistsByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct);

    /// <summary>True if any run for the named job exists within a workflow (correlation) instance.</summary>
    Task<bool> AnyRunInWorkflowAsync(string jobName, string correlationId, CancellationToken ct);

    /// <summary>True if the named job has a Succeeded run within a workflow (correlation) instance.</summary>
    Task<bool> SucceededInWorkflowAsync(string jobName, string correlationId, CancellationToken ct);

    Task<int> CountByStateAsync(RunState state, CancellationToken ct);

    Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

public interface IWorkerRegistry
{
    Task RegisterAsync(string nodeId, string hostname, IReadOnlyList<string> tags, int maxConcurrency, DateTimeOffset now, CancellationToken ct);
    Task<bool> HeartbeatAsync(string nodeId, DateTimeOffset now, CancellationToken ct);
    Task BeginDrainAsync(string nodeId, DateTimeOffset now, CancellationToken ct);
    Task<WorkerNode?> GetAsync(string nodeId, CancellationToken ct);
    Task<IReadOnlyList<WorkerNode>> ListAsync(CancellationToken ct);

    /// <summary>Marks nodes whose heartbeat is older than the TTL as dead. Returns count.</summary>
    Task<int> ReapDeadNodesAsync(DateTimeOffset now, TimeSpan ttl, CancellationToken ct);
}

public interface ILeaderElectionStore
{
    /// <summary>Attempts to acquire or renew leadership. Returns the current lease view.</summary>
    Task<LeaderView> TryAcquireOrRenewAsync(string nodeId, Guid token, DateTimeOffset now, TimeSpan ttl, CancellationToken ct);
    Task ReleaseAsync(string nodeId, Guid token, CancellationToken ct);
    Task<LeaderView> GetAsync(DateTimeOffset now, CancellationToken ct);
}

/// <summary>Read model of the leadership state.</summary>
public sealed record LeaderView(string? Owner, long FencingToken, DateTimeOffset? AcquiredAt, DateTimeOffset ExpiresAt, bool IsHeld);

public interface IDeadLetterStore
{
    Task AddAsync(DeadLetterEntry entry, CancellationToken ct);
    Task<PagedResult<DeadLetterEntry>> ListAsync(PageRequest page, bool includeReplayed, CancellationToken ct);
    Task<DeadLetterEntry?> GetAsync(Guid id, CancellationToken ct);
    Task<int> CountAsync(bool includeReplayed, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

public interface IRunLogStore
{
    Task AppendAsync(RunLog log, CancellationToken ct);
    Task AppendRangeAsync(IEnumerable<RunLog> logs, CancellationToken ct);
    Task<IReadOnlyList<RunLog>> ForRunAsync(Guid runId, CancellationToken ct);
    Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct);
}
