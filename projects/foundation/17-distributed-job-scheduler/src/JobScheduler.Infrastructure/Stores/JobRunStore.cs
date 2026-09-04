using JobScheduler.Application.Abstractions;
using JobScheduler.Domain;
using JobScheduler.Domain.Entities;
using JobScheduler.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JobScheduler.Infrastructure.Stores;

/// <summary>
/// EF Core implementation of the run store. The racy coordination operations (claim, start,
/// heartbeat, complete, reclaim) are implemented as single conditional <c>UPDATE</c>s via
/// <c>ExecuteUpdateAsync</c> so they are atomic at the database level — no read-modify-write window.
/// </summary>
public sealed class JobRunStore(AppDbContext db) : IJobRunStore
{
    private static readonly RunState[] ActiveStates = [RunState.Claimed, RunState.Running];

    public Task<JobRun?> GetAsync(Guid id, CancellationToken ct) =>
        db.JobRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct)!;

    public async Task AddAsync(JobRun run, CancellationToken ct) =>
        await db.JobRuns.AddAsync(run, ct);

    public async Task AddRangeAsync(IEnumerable<JobRun> runs, CancellationToken ct) =>
        await db.JobRuns.AddRangeAsync(runs, ct);

    public async Task<PagedResult<JobRun>> ListAsync(RunQuery query, PageRequest page, CancellationToken ct)
    {
        var q = db.JobRuns.AsNoTracking().AsQueryable();

        if (query.JobDefinitionId is { } defId) q = q.Where(r => r.JobDefinitionId == defId);
        if (query.State is { } state) q = q.Where(r => r.State == state);
        if (!string.IsNullOrWhiteSpace(query.Queue)) q = q.Where(r => r.Queue == query.Queue);
        if (!string.IsNullOrWhiteSpace(query.CorrelationId)) q = q.Where(r => r.CorrelationId == query.CorrelationId);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var s = query.Search;
            q = q.Where(r => r.JobName.Contains(s) || r.HandlerType.Contains(s) || r.IdempotencyKey.Contains(s));
        }

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(r => r.ScheduledAt)
            .Skip(page.Skip).Take(page.PageSize)
            .ToListAsync(ct);

        return new PagedResult<JobRun>(items, page.Page, page.PageSize, total);
    }

    public async Task<IReadOnlyList<JobRun>> GetDueAsync(
        DateTimeOffset now, double agingBoostPerMinute, int limit, CancellationToken ct)
    {
        // Fetch an over-sample ordered by base priority, then re-order in memory to apply aging.
        var candidates = await db.JobRuns.AsNoTracking()
            .Where(r => r.State == RunState.Pending && r.ScheduledAt <= now)
            .OrderByDescending(r => r.Priority)
            .ThenBy(r => r.ScheduledAt)
            .Take(Math.Max(limit * 4, limit))
            .ToListAsync(ct);

        return PriorityScheduler
            .Order(candidates, r => r.Priority, r => r.ScheduledAt, now, agingBoostPerMinute)
            .Take(limit)
            .ToList();
    }

    public async Task<ClaimResult> TryClaimAsync(
        Guid runId, string nodeId, Guid leaseToken, DateTimeOffset now, TimeSpan leaseDuration, bool singleton, CancellationToken ct)
    {
        var expires = now + leaseDuration;
        // The claim is a single conditional UPDATE. For singleton definitions we additionally require
        // that no sibling of the same definition is active; SQLite serialises writers so two
        // concurrent singleton claims cannot both observe "no active sibling".
        int affected = await db.JobRuns
            .Where(r => r.Id == runId && r.State == RunState.Pending && r.ScheduledAt <= now
                        && (!singleton || !db.JobRuns.Any(o => o.JobDefinitionId == r.JobDefinitionId
                                                               && o.Id != r.Id
                                                               && (o.State == RunState.Claimed || o.State == RunState.Running))))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.State, RunState.Claimed)
                .SetProperty(r => r.LeaseOwner, nodeId)
                .SetProperty(r => r.LeaseToken, leaseToken)
                .SetProperty(r => r.LeaseExpiresAt, expires)
                .SetProperty(r => r.FencingToken, r => r.FencingToken + 1)
                .SetProperty(r => r.Version, r => r.Version + 1), ct);

        if (affected == 0)
        {
            return ClaimResult.Lost;
        }

        var run = await db.JobRuns.AsNoTracking().FirstAsync(r => r.Id == runId, ct);
        return new ClaimResult(true, run, run.FencingToken);
    }

    public async Task<bool> TryStartAsync(Guid runId, Guid leaseToken, DateTimeOffset now, CancellationToken ct)
    {
        int affected = await db.JobRuns
            .Where(r => r.Id == runId && r.State == RunState.Claimed && r.LeaseToken == leaseToken)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.State, RunState.Running)
                .SetProperty(r => r.StartedAt, now)
                .SetProperty(r => r.AttemptCount, r => r.AttemptCount + 1)
                .SetProperty(r => r.Version, r => r.Version + 1), ct);
        return affected == 1;
    }

    public async Task<HeartbeatOutcome> HeartbeatAsync(Guid runId, Guid leaseToken, DateTimeOffset newExpiry, CancellationToken ct)
    {
        int affected = await db.JobRuns
            .Where(r => r.Id == runId && r.LeaseToken == leaseToken &&
                        (r.State == RunState.Claimed || r.State == RunState.Running))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.LeaseExpiresAt, newExpiry), ct);

        if (affected == 0)
        {
            return HeartbeatOutcome.Lost;
        }

        bool cancel = await db.JobRuns.AsNoTracking()
            .Where(r => r.Id == runId)
            .Select(r => r.CancelRequested)
            .FirstOrDefaultAsync(ct);
        return new HeartbeatOutcome(true, cancel);
    }

    public async Task<bool> RequestCancelAsync(Guid runId, DateTimeOffset now, CancellationToken ct)
    {
        var run = await db.JobRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null || RunStateMachine.IsTerminal(run.State))
        {
            return false;
        }

        if (run.State == RunState.Pending)
        {
            run.Cancel(now);
        }
        else
        {
            run.RequestCancel();
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> TryCompleteAsync(
        Guid runId, long fencingToken, RunState finalState, string? output, string? error, DateTimeOffset now, CancellationToken ct)
    {
        // Guarded by the fencing token AND the Running state: a stalled, superseded worker whose
        // token no longer matches (because the run was reclaimed and re-claimed) is rejected.
        int affected = await db.JobRuns
            .Where(r => r.Id == runId && r.FencingToken == fencingToken && r.State == RunState.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.State, finalState)
                .SetProperty(r => r.Output, output)
                .SetProperty(r => r.Error, error)
                .SetProperty(r => r.FinishedAt, now)
                .SetProperty(r => r.LeaseOwner, (string?)null)
                .SetProperty(r => r.LeaseToken, (Guid?)null)
                .SetProperty(r => r.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(r => r.Version, r => r.Version + 1), ct);
        return affected == 1;
    }

    public async Task<int> ReclaimExpiredLeasesAsync(DateTimeOffset now, CancellationToken ct)
    {
        return await db.JobRuns
            .Where(r => (r.State == RunState.Claimed || r.State == RunState.Running)
                        && r.LeaseExpiresAt != null && r.LeaseExpiresAt <= now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.State, RunState.Pending)
                .SetProperty(r => r.LeaseOwner, (string?)null)
                .SetProperty(r => r.LeaseToken, (Guid?)null)
                .SetProperty(r => r.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(r => r.Version, r => r.Version + 1), ct);
    }

    public async Task ScheduleRetryAsync(Guid runId, DateTimeOffset nextScheduledAt, CancellationToken ct)
    {
        var run = await db.JobRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null)
        {
            return;
        }
        run.Retry(nextScheduledAt);
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkDeadLetteredAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.JobRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null)
        {
            return;
        }
        run.DeadLetter();
        await db.SaveChangesAsync(ct);
    }

    public async Task<JobRun?> ReplayResetAsync(Guid runId, DateTimeOffset scheduledAt, CancellationToken ct)
    {
        var run = await db.JobRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null)
        {
            return null;
        }
        run.ReplayReset(scheduledAt);
        await db.SaveChangesAsync(ct);
        return run;
    }

    public Task<int> CountActiveGlobalAsync(CancellationToken ct) =>
        db.JobRuns.CountAsync(r => r.State == RunState.Claimed || r.State == RunState.Running, ct);

    public Task<int> CountActiveByDefinitionAsync(Guid jobDefinitionId, CancellationToken ct) =>
        db.JobRuns.CountAsync(r => r.JobDefinitionId == jobDefinitionId &&
                                   (r.State == RunState.Claimed || r.State == RunState.Running), ct);

    public Task<int> CountActiveByQueueAsync(string queue, CancellationToken ct) =>
        db.JobRuns.CountAsync(r => r.Queue == queue &&
                                   (r.State == RunState.Claimed || r.State == RunState.Running), ct);

    public Task<bool> HasActiveForDefinitionAsync(Guid jobDefinitionId, CancellationToken ct) =>
        db.JobRuns.AnyAsync(r => r.JobDefinitionId == jobDefinitionId &&
                                 (r.State == RunState.Claimed || r.State == RunState.Running), ct);

    public Task<bool> ExistsByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct) =>
        db.JobRuns.AnyAsync(r => r.IdempotencyKey == idempotencyKey, ct);

    public Task<bool> AnyRunInWorkflowAsync(string jobName, string correlationId, CancellationToken ct) =>
        db.JobRuns.AnyAsync(r => r.JobName == jobName && r.CorrelationId == correlationId, ct);

    public Task<bool> SucceededInWorkflowAsync(string jobName, string correlationId, CancellationToken ct) =>
        db.JobRuns.AnyAsync(r => r.JobName == jobName && r.CorrelationId == correlationId &&
                                 r.State == RunState.Succeeded, ct);

    public Task<int> CountByStateAsync(RunState state, CancellationToken ct) =>
        db.JobRuns.CountAsync(r => r.State == state, ct);

    public async Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct)
    {
        return await db.JobRuns
            .Where(r => (r.State == RunState.Succeeded || r.State == RunState.Cancelled)
                        && r.FinishedAt != null && r.FinishedAt < olderThan)
            .ExecuteDeleteAsync(ct);
    }

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
