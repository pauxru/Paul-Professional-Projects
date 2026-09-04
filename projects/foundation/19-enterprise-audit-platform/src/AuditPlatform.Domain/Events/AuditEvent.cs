using System.ComponentModel.DataAnnotations.Schema;

namespace AuditPlatform.Domain.Events;

/// <summary>
/// Append-only audit record. Once persisted, an <see cref="AuditEvent"/> is
/// structurally immutable — the EF SaveChanges interceptor rejects any
/// Modified or Deleted state on this entity, and every scalar setter here
/// is private so accidental mutation cannot compile.
///
/// The <see cref="ChainHash"/> is the SHA-256 of the canonical JSON of the
/// event content concatenated with the previous chain hash for the same
/// tenant. That is the invariant the whole integrity story relies on.
/// </summary>
public sealed class AuditEvent
{
    // Primary key: a UUIDv7 so rows sort naturally by event time.
    public Guid Id { get; private set; }

    // Tenant is required. All queries are filtered by TenantId by design.
    public string TenantId { get; private set; } = string.Empty;

    // The canonical event-type name registered in the schema registry (e.g. "user.login").
    public string EventType { get; private set; } = string.Empty;
    public int SchemaVersion { get; private set; }

    // When the caller says the event happened.
    public DateTimeOffset EventTime { get; private set; }

    // When we accepted and persisted it.
    public DateTimeOffset IngestTime { get; private set; }

    public ActorType ActorType { get; private set; }
    public string ActorId { get; private set; } = string.Empty;
    public string ActorDisplayName { get; private set; } = string.Empty;
    // Roles held at the moment of the action, canonicalised inside PayloadJson.
    public string ActorRolesCsv { get; private set; } = string.Empty;

    public string ActionVerb { get; private set; } = string.Empty;
    public EventCategory Category { get; private set; }

    public string ResourceType { get; private set; } = string.Empty;
    public string ResourceId { get; private set; } = string.Empty;
    public string ResourceName { get; private set; } = string.Empty;
    public string ResourceParentPath { get; private set; } = string.Empty;

    public EventOutcome Outcome { get; private set; }
    public EventSeverity Severity { get; private set; }

    public string SourceIp { get; private set; } = string.Empty;
    public string SourceUserAgent { get; private set; } = string.Empty;
    public string SourceService { get; private set; } = string.Empty;
    public string SourceRegion { get; private set; } = string.Empty;

    public string CorrelationId { get; private set; } = string.Empty;
    public string? CausationId { get; private set; }
    public string TraceId { get; private set; } = string.Empty;

    // Optional caller-supplied idempotency key. Together with (TenantId, ClientEventId) it forms
    // the natural key used for idempotent ingest replays.
    public string? ClientEventId { get; private set; }

    // Full canonical JSON of the entire event content (including before/after). This is what
    // hashes into ChainHash. Storing it lets us recompute and verify at any later point.
    public string PayloadJson { get; private set; } = string.Empty;

    // SHA-256 hash of PayloadJson (i.e. of the event content alone, not the chain).
    public string ContentHash { get; private set; } = string.Empty;

    // SHA-256(canonical(PayloadJson) || previousChainHash) — the chain link.
    public string ChainHash { get; private set; } = string.Empty;

    // Previous event's chain hash — the last block's hash for the same tenant.
    public string PreviousChainHash { get; private set; } = string.Empty;

    // Position of this event in the tenant's chain, starting at 1.
    public long SequenceNumber { get; private set; }

    // Hashes of before/after so redacted reads can still prove integrity to a knowledgeable reader.
    public string? BeforeHash { get; private set; }
    public string? AfterHash { get; private set; }

    // Retention pruning support. When true, PayloadJson is a tombstone, but ChainHash / PreviousChainHash
    // are preserved so the chain still verifies. Tombstoned events remain queryable at the metadata level.
    public bool IsTombstoned { get; private set; }
    public DateTimeOffset? TombstonedAt { get; private set; }

    // EF Core needs a parameterless constructor.
    private AuditEvent() { }

    /// <summary>
    /// Factory used exclusively by the ingest service so that the invariants of chaining and
    /// hashing are the only route into a persisted event.
    /// </summary>
    public static AuditEvent CreateInternal(
        Guid id,
        string tenantId,
        string eventType,
        int schemaVersion,
        DateTimeOffset eventTime,
        DateTimeOffset ingestTime,
        ActorType actorType,
        string actorId,
        string actorDisplayName,
        string actorRolesCsv,
        string actionVerb,
        EventCategory category,
        string resourceType,
        string resourceId,
        string resourceName,
        string resourceParentPath,
        EventOutcome outcome,
        EventSeverity severity,
        string sourceIp,
        string sourceUserAgent,
        string sourceService,
        string sourceRegion,
        string correlationId,
        string? causationId,
        string traceId,
        string? clientEventId,
        string payloadJson,
        string contentHash,
        string chainHash,
        string previousChainHash,
        long sequenceNumber,
        string? beforeHash,
        string? afterHash)
    {
        return new AuditEvent
        {
            Id = id,
            TenantId = tenantId,
            EventType = eventType,
            SchemaVersion = schemaVersion,
            EventTime = eventTime,
            IngestTime = ingestTime,
            ActorType = actorType,
            ActorId = actorId,
            ActorDisplayName = actorDisplayName,
            ActorRolesCsv = actorRolesCsv,
            ActionVerb = actionVerb,
            Category = category,
            ResourceType = resourceType,
            ResourceId = resourceId,
            ResourceName = resourceName,
            ResourceParentPath = resourceParentPath,
            Outcome = outcome,
            Severity = severity,
            SourceIp = sourceIp,
            SourceUserAgent = sourceUserAgent,
            SourceService = sourceService,
            SourceRegion = sourceRegion,
            CorrelationId = correlationId,
            CausationId = causationId,
            TraceId = traceId,
            ClientEventId = clientEventId,
            PayloadJson = payloadJson,
            ContentHash = contentHash,
            ChainHash = chainHash,
            PreviousChainHash = previousChainHash,
            SequenceNumber = sequenceNumber,
            BeforeHash = beforeHash,
            AfterHash = afterHash,
            IsTombstoned = false,
            TombstonedAt = null
        };
    }

    /// <summary>
    /// Convert this event into a tombstone by clearing PayloadJson while keeping ChainHash and
    /// PreviousChainHash intact. The hash chain still verifies afterwards, but the payload
    /// content itself is gone — this is the reconciliation between retention and immutability.
    /// This is the ONLY permitted structural change to a persisted audit event.
    /// </summary>
    public void Tombstone(DateTimeOffset when)
    {
        if (IsTombstoned) return;
        PayloadJson = TombstonePayloadForContentHash(ContentHash);
        IsTombstoned = true;
        TombstonedAt = when;
    }

    public static string TombstonePayloadForContentHash(string contentHash)
        => "{\"tombstone\":true,\"contentHash\":\"" + contentHash + "\"}";
}
