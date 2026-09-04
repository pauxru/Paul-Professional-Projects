namespace NotificationPlatform.Domain.Suppressions;

using NotificationPlatform.Domain.Common;

public sealed class SuppressionEntry
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public NotificationChannel Channel { get; private set; }
    public string Address { get; private set; } = string.Empty;
    public SuppressionReason Reason { get; private set; }
    public string? Notes { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private SuppressionEntry() { }

    public SuppressionEntry(Guid id, Guid tenantId, NotificationChannel channel, string address, SuppressionReason reason, string? notes, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty) throw new ArgumentException("id required", nameof(id));
        if (tenantId == Guid.Empty) throw new ArgumentException("tenantId required", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("address required", nameof(address));
        Id = id;
        TenantId = tenantId;
        Channel = channel;
        Address = NormalizeAddress(channel, address);
        Reason = reason;
        Notes = notes;
        CreatedAt = createdAt;
    }

    public static string NormalizeAddress(NotificationChannel channel, string address)
    {
        if (channel == NotificationChannel.Email) return address.Trim().ToLowerInvariant();
        return address.Trim();
    }
}
