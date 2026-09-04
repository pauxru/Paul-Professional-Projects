using AuditPlatform.Domain.Events;
using AuditPlatform.Domain.Integrity;
using AuditPlatform.Domain.Retention;
using AuditPlatform.Domain.Schemas;

namespace AuditPlatform.Application.Abstractions;

public interface IAuditEventStore
{
    Task<AuditEvent?> GetLatestForTenantAsync(string tenantId, CancellationToken ct);
    Task<AuditEvent?> GetByIdAsync(string tenantId, Guid id, CancellationToken ct);
    Task<AuditEvent?> GetByClientEventIdAsync(string tenantId, string clientEventId, CancellationToken ct);
    Task AppendAsync(AuditEvent evt, CancellationToken ct);
    Task<IReadOnlyList<AuditEvent>> ListForVerificationAsync(string tenantId, long fromSequence, long toSequence, CancellationToken ct);
    IQueryable<AuditEvent> Query(string tenantId);
    Task SaveChangesAsync(CancellationToken ct);
}

public interface ICheckpointStore
{
    Task AddAsync(Checkpoint checkpoint, CancellationToken ct);
    Task<Checkpoint?> LatestForTenantAsync(string tenantId, CancellationToken ct);
    Task<IReadOnlyList<Checkpoint>> ListForTenantAsync(string tenantId, CancellationToken ct);
    Task<Checkpoint?> FindCheckpointForSequenceAsync(string tenantId, long sequence, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

public interface ISchemaRegistry
{
    Task<EventSchema?> GetLatestAsync(string eventType, CancellationToken ct);
    Task<EventSchema?> GetAsync(string eventType, int version, CancellationToken ct);
    Task<IReadOnlyList<EventSchema>> ListAsync(CancellationToken ct);
    Task<EventSchema> RegisterAsync(string eventType, string schemaJson, string description, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

public interface IRetentionStore
{
    Task<IReadOnlyList<RetentionPolicy>> ListAsync(string tenantId, CancellationToken ct);
    Task AddAsync(RetentionPolicy policy, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

public interface ILegalHoldStore
{
    Task<IReadOnlyList<LegalHold>> ListActiveAsync(string tenantId, CancellationToken ct);
    Task<LegalHold?> GetAsync(Guid id, CancellationToken ct);
    Task AddAsync(LegalHold hold, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

public interface ISavedQueryStore
{
    Task<IReadOnlyList<SavedQuery>> ListAsync(string tenantId, CancellationToken ct);
    Task AddAsync(SavedQuery query, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

public interface IDeadLetterStore
{
    Task AddAsync(DeadLetterEvent dle, CancellationToken ct);
    Task<IReadOnlyList<DeadLetterEvent>> ListAsync(string tenantId, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

public interface ISigningService
{
    string KeyId { get; }
    string SignBase64(byte[] payload);
    bool Verify(byte[] payload, string signatureBase64);
}

public interface ISearchIndex
{
    /// <summary>Return the ids of events whose text matches the query. Bounded result count for safety.</summary>
    Task<IReadOnlyList<Guid>> SearchAsync(string tenantId, string text, int limit, CancellationToken ct);
    Task IndexAsync(Guid eventId, string tenantId, string text, CancellationToken ct);
}

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct);
}
