namespace NotificationPlatform.Domain.Preferences;

using NotificationPlatform.Domain.Common;

public sealed class RecipientPreference
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid RecipientId { get; private set; }
    public NotificationChannel Channel { get; private set; }
    public NotificationCategory Category { get; private set; }
    public bool OptedIn { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private RecipientPreference() { }

    public RecipientPreference(Guid id, Guid tenantId, Guid recipientId, NotificationChannel channel, NotificationCategory category, bool optedIn, DateTimeOffset updatedAt)
    {
        Id = id;
        TenantId = tenantId;
        RecipientId = recipientId;
        Channel = channel;
        Category = category;
        OptedIn = optedIn;
        UpdatedAt = updatedAt;
    }

    public void Set(bool optedIn, DateTimeOffset at)
    {
        OptedIn = optedIn;
        UpdatedAt = at;
    }
}
