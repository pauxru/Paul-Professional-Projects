using JobScheduler.Application.Abstractions;
using JobScheduler.Domain;
using JobScheduler.Domain.Entities;
using JobScheduler.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JobScheduler.Infrastructure.Stores;

public sealed class JobDefinitionStore(AppDbContext db) : IJobDefinitionStore
{
    public Task<JobDefinition?> GetAsync(Guid id, CancellationToken ct) =>
        db.JobDefinitions.FirstOrDefaultAsync(d => d.Id == id, ct)!;

    public Task<JobDefinition?> GetByNameAsync(string name, CancellationToken ct) =>
        db.JobDefinitions.FirstOrDefaultAsync(d => d.Name == name, ct)!;

    public async Task<PagedResult<JobDefinition>> ListAsync(PageRequest page, bool? enabledOnly, CancellationToken ct)
    {
        var q = db.JobDefinitions.AsNoTracking().AsQueryable();
        if (enabledOnly == true)
        {
            q = q.Where(d => d.Enabled);
        }
        var total = await q.CountAsync(ct);
        var items = await q.OrderBy(d => d.Name).Skip(page.Skip).Take(page.PageSize).ToListAsync(ct);
        return new PagedResult<JobDefinition>(items, page.Page, page.PageSize, total);
    }

    public async Task<IReadOnlyList<JobDefinition>> ListEnabledScheduledAsync(CancellationToken ct) =>
        await db.JobDefinitions
            .Where(d => d.Enabled && d.TriggerType != TriggerType.Manual)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<JobDefinition>> ListAllAsync(CancellationToken ct) =>
        await db.JobDefinitions.AsNoTracking().ToListAsync(ct);

    public async Task AddAsync(JobDefinition definition, CancellationToken ct) =>
        await db.JobDefinitions.AddAsync(definition, ct);

    public Task UpdateAsync(JobDefinition definition, CancellationToken ct)
    {
        db.JobDefinitions.Update(definition);
        return Task.CompletedTask;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        int affected = await db.JobDefinitions.Where(d => d.Id == id).ExecuteDeleteAsync(ct);
        return affected > 0;
    }

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}

public sealed class WorkerRegistry(AppDbContext db) : IWorkerRegistry
{
    public async Task RegisterAsync(
        string nodeId, string hostname, IReadOnlyList<string> tags, int maxConcurrency, DateTimeOffset now, CancellationToken ct)
    {
        var existing = await db.WorkerNodes.FirstOrDefaultAsync(n => n.NodeId == nodeId, ct);
        if (existing is null)
        {
            await db.WorkerNodes.AddAsync(WorkerNode.Register(nodeId, hostname, tags, maxConcurrency, now), ct);
        }
        else
        {
            existing.Heartbeat(now);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> HeartbeatAsync(string nodeId, DateTimeOffset now, CancellationToken ct)
    {
        int affected = await db.WorkerNodes
            .Where(n => n.NodeId == nodeId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.LastHeartbeat, now)
                .SetProperty(n => n.Status, n => n.Status == WorkerStatus.Draining ? WorkerStatus.Draining : WorkerStatus.Active), ct);
        return affected == 1;
    }

    public async Task BeginDrainAsync(string nodeId, DateTimeOffset now, CancellationToken ct)
    {
        await db.WorkerNodes
            .Where(n => n.NodeId == nodeId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.Status, WorkerStatus.Draining)
                .SetProperty(n => n.LastHeartbeat, now), ct);
    }

    public Task<WorkerNode?> GetAsync(string nodeId, CancellationToken ct) =>
        db.WorkerNodes.AsNoTracking().FirstOrDefaultAsync(n => n.NodeId == nodeId, ct)!;

    public async Task<IReadOnlyList<WorkerNode>> ListAsync(CancellationToken ct) =>
        await db.WorkerNodes.AsNoTracking().OrderBy(n => n.NodeId).ToListAsync(ct);

    public async Task<int> ReapDeadNodesAsync(DateTimeOffset now, TimeSpan ttl, CancellationToken ct)
    {
        var cutoff = now - ttl;
        return await db.WorkerNodes
            .Where(n => n.Status != WorkerStatus.Dead && n.LastHeartbeat < cutoff)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.Status, WorkerStatus.Dead), ct);
    }
}

public sealed class DeadLetterStore(AppDbContext db) : IDeadLetterStore
{
    public async Task AddAsync(DeadLetterEntry entry, CancellationToken ct) =>
        await db.DeadLetters.AddAsync(entry, ct);

    public async Task<PagedResult<DeadLetterEntry>> ListAsync(PageRequest page, bool includeReplayed, CancellationToken ct)
    {
        var q = db.DeadLetters.AsNoTracking().AsQueryable();
        if (!includeReplayed)
        {
            q = q.Where(d => !d.Replayed);
        }
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(d => d.DeadLetteredAt).Skip(page.Skip).Take(page.PageSize).ToListAsync(ct);
        return new PagedResult<DeadLetterEntry>(items, page.Page, page.PageSize, total);
    }

    public Task<DeadLetterEntry?> GetAsync(Guid id, CancellationToken ct) =>
        db.DeadLetters.FirstOrDefaultAsync(d => d.Id == id, ct)!;

    public Task<int> CountAsync(bool includeReplayed, CancellationToken ct) =>
        includeReplayed ? db.DeadLetters.CountAsync(ct) : db.DeadLetters.CountAsync(d => !d.Replayed, ct);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}

public sealed class RunLogStore(AppDbContext db) : IRunLogStore
{
    public async Task AppendAsync(RunLog log, CancellationToken ct)
    {
        await db.RunLogs.AddAsync(log, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task AppendRangeAsync(IEnumerable<RunLog> logs, CancellationToken ct)
    {
        await db.RunLogs.AddRangeAsync(logs, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<RunLog>> ForRunAsync(Guid runId, CancellationToken ct) =>
        await db.RunLogs.AsNoTracking().Where(l => l.JobRunId == runId).OrderBy(l => l.Timestamp).ThenBy(l => l.Id).ToListAsync(ct);

    public Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct) =>
        db.RunLogs.Where(l => l.Timestamp < olderThan).ExecuteDeleteAsync(ct);
}
