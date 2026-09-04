namespace AuditPlatform.Domain.Retention;

public sealed class RetentionPolicy
{
    public Guid Id { get; private set; }
    public string TenantId { get; private set; } = string.Empty;
    // A "*" wildcard applies to every category not otherwise matched.
    public string CategoryPattern { get; private set; } = "*";
    public int RetainForDays { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private RetentionPolicy() { }

    public static RetentionPolicy Create(Guid id, string tenantId, string categoryPattern, int retainForDays, DateTimeOffset when)
        => new() { Id = id, TenantId = tenantId, CategoryPattern = categoryPattern, RetainForDays = retainForDays, CreatedAt = when };
}

public sealed class LegalHold
{
    public Guid Id { get; private set; }
    public string TenantId { get; private set; } = string.Empty;
    public string ResourceType { get; private set; } = string.Empty;
    public string ResourceId { get; private set; } = string.Empty;
    public string Reason { get; private set; } = string.Empty;
    public string TicketReference { get; private set; } = string.Empty;
    public DateTimeOffset AppliedAt { get; private set; }
    public DateTimeOffset? ReleasedAt { get; private set; }
    public bool IsActive => ReleasedAt is null;

    private LegalHold() { }

    public static LegalHold Apply(Guid id, string tenantId, string resourceType, string resourceId, string reason, string ticket, DateTimeOffset when)
        => new()
        {
            Id = id,
            TenantId = tenantId,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Reason = reason,
            TicketReference = ticket,
            AppliedAt = when
        };

    public void Release(DateTimeOffset when)
    {
        ReleasedAt ??= when;
    }
}
