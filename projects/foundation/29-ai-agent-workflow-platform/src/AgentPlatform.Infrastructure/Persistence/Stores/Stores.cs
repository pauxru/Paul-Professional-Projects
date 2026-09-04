using AgentPlatform.Application.Abstractions;
using AgentPlatform.Domain.Approvals;
using AgentPlatform.Domain.Runs;
using AgentPlatform.Domain.Tracing;
using AgentPlatform.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Infrastructure.Persistence.Stores;

/// <summary>Commits all staged changes on the shared <see cref="AgentDbContext"/>.</summary>
public sealed class EfUnitOfWork : IUnitOfWork
{
    private readonly AgentDbContext _db;

    public EfUnitOfWork(AgentDbContext db) => _db = db;

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => _db.SaveChangesAsync(cancellationToken);
}

/// <summary>EF-backed persistence for runs, their step executions and their trace events.</summary>
public sealed class RunStore : IRunStore
{
    private readonly AgentDbContext _db;

    public RunStore(AgentDbContext db) => _db = db;

    public void Add(WorkflowRun run) => _db.Runs.Add(run);

    public async Task<WorkflowRun?> GetAsync(string runId, CancellationToken cancellationToken) =>
        await _db.Runs.Include(r => r.StepExecutions).FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);

    public async Task<WorkflowRun?> GetByIdempotencyKeyAsync(string tenantId, string idempotencyKey, CancellationToken cancellationToken) =>
        await _db.Runs.FirstOrDefaultAsync(r => r.TenantId == tenantId && r.IdempotencyKey == idempotencyKey, cancellationToken);

    public void AddStepExecution(StepExecution execution) => _db.StepExecutions.Add(execution);

    public void AddTraceEvent(TraceEvent traceEvent) => _db.TraceEvents.Add(traceEvent);

    public async Task<IReadOnlyList<TraceEvent>> GetTraceAsync(string runId, CancellationToken cancellationToken) =>
        await _db.TraceEvents.Where(t => t.RunId == runId).OrderBy(t => t.Ordinal)
            .AsNoTracking().ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<WorkflowRun>> ListAsync(string? tenantId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = _db.Runs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(tenantId)) query = query.Where(r => r.TenantId == tenantId);
        return await query.OrderByDescending(r => r.CreatedAt)
            .Skip(Math.Max(0, page) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
    }

    public async Task<int> CountAsync(string? tenantId, CancellationToken cancellationToken)
    {
        var query = _db.Runs.AsQueryable();
        if (!string.IsNullOrEmpty(tenantId)) query = query.Where(r => r.TenantId == tenantId);
        return await query.CountAsync(cancellationToken);
    }
}

/// <summary>EF-backed persistence for approval tasks.</summary>
public sealed class ApprovalStore : IApprovalStore
{
    private readonly AgentDbContext _db;

    public ApprovalStore(AgentDbContext db) => _db = db;

    public void Add(ApprovalTask approval) => _db.Approvals.Add(approval);

    public async Task<ApprovalTask?> GetAsync(string approvalId, CancellationToken cancellationToken) =>
        await _db.Approvals.FirstOrDefaultAsync(a => a.Id == approvalId, cancellationToken);

    public async Task<ApprovalTask?> GetPendingForRunAsync(string runId, CancellationToken cancellationToken) =>
        await _db.Approvals.Where(a => a.RunId == runId && a.Status == ApprovalStatus.Pending)
            .OrderByDescending(a => a.RequestedAt).FirstOrDefaultAsync(cancellationToken);

    public async Task<ApprovalTask?> GetLatestForRunAsync(string runId, CancellationToken cancellationToken) =>
        await _db.Approvals.Where(a => a.RunId == runId)
            .OrderByDescending(a => a.RequestedAt).FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<ApprovalTask>> ListPendingAsync(string? tenantId, CancellationToken cancellationToken)
    {
        var query = _db.Approvals.AsNoTracking().Where(a => a.Status == ApprovalStatus.Pending);
        if (!string.IsNullOrEmpty(tenantId)) query = query.Where(a => a.TenantId == tenantId);
        return await query.OrderBy(a => a.RequestedAt).ToListAsync(cancellationToken);
    }
}

/// <summary>
/// EF-backed idempotency store. The <see cref="Add"/> only stages a record; the caller
/// (the tool invoker) commits it atomically with the tool's side effect.
/// </summary>
public sealed class IdempotencyStore : IIdempotencyStore
{
    private readonly AgentDbContext _db;

    public IdempotencyStore(AgentDbContext db) => _db = db;

    public async Task<string?> TryGetResultAsync(string key, CancellationToken cancellationToken)
    {
        var record = await _db.IdempotencyRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Key == key, cancellationToken);
        return record?.ResultJson;
    }

    public void Add(string key, string runId, string toolName, string resultJson, DateTimeOffset now) =>
        _db.IdempotencyRecords.Add(new IdempotencyRecord
        {
            Key = key,
            RunId = runId,
            ToolName = toolName,
            ResultJson = resultJson,
            CreatedAt = now,
        });
}
