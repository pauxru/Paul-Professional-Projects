using AuditPlatform.Application.Abstractions;
using AuditPlatform.Domain.Events;
using AuditPlatform.Domain.Integrity;
using AuditPlatform.Domain.Retention;
using AuditPlatform.Domain.Schemas;
using AuditPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuditPlatform.Infrastructure.Events;

public sealed class AuditEventStore : IAuditEventStore
{
    private readonly AppDbContext _db;
    public AuditEventStore(AppDbContext db) => _db = db;

    public Task<AuditEvent?> GetLatestForTenantAsync(string tenantId, CancellationToken ct)
        => _db.Events.AsNoTracking().Where(e => e.TenantId == tenantId)
            .OrderByDescending(e => e.SequenceNumber).FirstOrDefaultAsync(ct);

    public Task<AuditEvent?> GetByIdAsync(string tenantId, Guid id, CancellationToken ct)
        => _db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == tenantId && e.Id == id, ct);

    public Task<AuditEvent?> GetByClientEventIdAsync(string tenantId, string clientEventId, CancellationToken ct)
        => _db.Events.AsNoTracking()
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.ClientEventId == clientEventId, ct);

    public Task AppendAsync(AuditEvent evt, CancellationToken ct)
    {
        _db.Events.Add(evt);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<AuditEvent>> ListForVerificationAsync(string tenantId, long fromSequence, long toSequence, CancellationToken ct)
    {
        return await _db.Events.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.SequenceNumber >= fromSequence && e.SequenceNumber <= toSequence)
            .OrderBy(e => e.SequenceNumber)
            .ToListAsync(ct);
    }

    public IQueryable<AuditEvent> Query(string tenantId) => _db.Events.Where(e => e.TenantId == tenantId);

    public Task SaveChangesAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}

public sealed class CheckpointStore : ICheckpointStore
{
    private readonly AppDbContext _db;
    public CheckpointStore(AppDbContext db) => _db = db;

    public Task AddAsync(Checkpoint checkpoint, CancellationToken ct)
    {
        _db.Checkpoints.Add(checkpoint);
        return Task.CompletedTask;
    }

    public Task<Checkpoint?> LatestForTenantAsync(string tenantId, CancellationToken ct)
        => _db.Checkpoints.AsNoTracking().Where(c => c.TenantId == tenantId)
            .OrderByDescending(c => c.ToSequence).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<Checkpoint>> ListForTenantAsync(string tenantId, CancellationToken ct)
        => await _db.Checkpoints.AsNoTracking().Where(c => c.TenantId == tenantId)
            .OrderBy(c => c.FromSequence).ToListAsync(ct);

    public Task<Checkpoint?> FindCheckpointForSequenceAsync(string tenantId, long sequence, CancellationToken ct)
        => _db.Checkpoints.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.FromSequence <= sequence && c.ToSequence >= sequence)
            .OrderByDescending(c => c.CreatedAt).FirstOrDefaultAsync(ct);

    public Task SaveChangesAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}

public sealed class EventSchemaRegistry : ISchemaRegistry
{
    private readonly AppDbContext _db;
    public EventSchemaRegistry(AppDbContext db) => _db = db;

    public async Task<EventSchema?> GetLatestAsync(string eventType, CancellationToken ct)
        => await _db.Schemas.AsNoTracking().Where(s => s.EventType == eventType)
            .OrderByDescending(s => s.Version).FirstOrDefaultAsync(ct);

    public Task<EventSchema?> GetAsync(string eventType, int version, CancellationToken ct)
        => _db.Schemas.AsNoTracking().FirstOrDefaultAsync(s => s.EventType == eventType && s.Version == version, ct);

    public async Task<IReadOnlyList<EventSchema>> ListAsync(CancellationToken ct)
        => await _db.Schemas.AsNoTracking().OrderBy(s => s.EventType).ThenBy(s => s.Version).ToListAsync(ct);

    public async Task<EventSchema> RegisterAsync(string eventType, string schemaJson, string description, CancellationToken ct)
    {
        var latest = await _db.Schemas.AsNoTracking().Where(s => s.EventType == eventType)
            .OrderByDescending(s => s.Version).FirstOrDefaultAsync(ct);
        var nextVersion = (latest?.Version ?? 0) + 1;
        var schema = EventSchema.Create(Guid.NewGuid(), eventType, nextVersion, schemaJson, description, DateTimeOffset.UtcNow);
        _db.Schemas.Add(schema);
        await _db.SaveChangesAsync(ct);
        return schema;
    }

    public Task SaveChangesAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}

public sealed class RetentionStore : IRetentionStore
{
    private readonly AppDbContext _db;
    public RetentionStore(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<RetentionPolicy>> ListAsync(string tenantId, CancellationToken ct)
        => await _db.RetentionPolicies.AsNoTracking().Where(p => p.TenantId == tenantId).ToListAsync(ct);

    public Task AddAsync(RetentionPolicy policy, CancellationToken ct)
    {
        _db.RetentionPolicies.Add(policy);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}

public sealed class LegalHoldStore : ILegalHoldStore
{
    private readonly AppDbContext _db;
    public LegalHoldStore(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<LegalHold>> ListActiveAsync(string tenantId, CancellationToken ct)
        => await _db.LegalHolds.AsNoTracking().Where(h => h.TenantId == tenantId && h.ReleasedAt == null).ToListAsync(ct);

    public Task<LegalHold?> GetAsync(Guid id, CancellationToken ct)
        => _db.LegalHolds.FirstOrDefaultAsync(h => h.Id == id, ct);

    public Task AddAsync(LegalHold hold, CancellationToken ct)
    {
        _db.LegalHolds.Add(hold);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}

public sealed class SavedQueryStore : ISavedQueryStore
{
    private readonly AppDbContext _db;
    public SavedQueryStore(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<SavedQuery>> ListAsync(string tenantId, CancellationToken ct)
        => await _db.SavedQueries.AsNoTracking().Where(s => s.TenantId == tenantId).ToListAsync(ct);

    public Task AddAsync(SavedQuery query, CancellationToken ct)
    {
        _db.SavedQueries.Add(query);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}

public sealed class DeadLetterStore : IDeadLetterStore
{
    private readonly AppDbContext _db;
    public DeadLetterStore(AppDbContext db) => _db = db;

    public Task AddAsync(DeadLetterEvent dle, CancellationToken ct)
    {
        _db.DeadLetter.Add(dle);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<DeadLetterEvent>> ListAsync(string tenantId, CancellationToken ct)
        => await _db.DeadLetter.AsNoTracking().Where(d => d.TenantId == tenantId).OrderByDescending(d => d.ReceivedAt).ToListAsync(ct);

    public Task SaveChangesAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}
