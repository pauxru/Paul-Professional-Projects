namespace AuditPlatform.Domain.Events;

/// <summary>
/// Saved query for reuse in dashboards or scheduled reports. Serialised as JSON so we can
/// evolve the query language without a schema migration on this table.
/// </summary>
public sealed class SavedQuery
{
    public Guid Id { get; private set; }
    public string TenantId { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string QueryJson { get; private set; } = string.Empty;
    public string CreatedByActorId { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }

    private SavedQuery() { }

    public static SavedQuery Create(Guid id, string tenantId, string name, string queryJson, string actorId, DateTimeOffset when)
        => new() { Id = id, TenantId = tenantId, Name = name, QueryJson = queryJson, CreatedByActorId = actorId, CreatedAt = when };
}

public sealed class DeadLetterEvent
{
    public Guid Id { get; private set; }
    public string TenantId { get; private set; } = string.Empty;
    public string RawPayload { get; private set; } = string.Empty;
    public string Reason { get; private set; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; private set; }

    private DeadLetterEvent() { }

    public static DeadLetterEvent Record(Guid id, string tenantId, string raw, string reason, DateTimeOffset when)
        => new() { Id = id, TenantId = tenantId, RawPayload = raw, Reason = reason, ReceivedAt = when };
}
