namespace NotificationPlatform.Domain.Recipients;

using NotificationPlatform.Domain.Common;

public sealed class Recipient
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string ExternalId { get; private set; } = string.Empty;
    public string? Email { get; private set; }
    public string? PhoneE164 { get; private set; }
    public string? PushToken { get; private set; }
    public string? WebhookUrl { get; private set; }
    public string Locale { get; private set; } = "en";
    public string TimeZoneId { get; private set; } = "UTC";
    public TimeSpan QuietHoursStart { get; private set; } = TimeSpan.Zero;
    public TimeSpan QuietHoursEnd { get; private set; } = TimeSpan.Zero;
    public string? FirstName { get; private set; }
    public string? LastName { get; private set; }

    private Recipient() { }

    public Recipient(
        Guid id,
        Guid tenantId,
        string externalId,
        string? email,
        string? phoneE164,
        string? pushToken,
        string? webhookUrl,
        string locale,
        string timeZoneId,
        TimeSpan quietHoursStart,
        TimeSpan quietHoursEnd,
        string? firstName = null,
        string? lastName = null)
    {
        if (id == Guid.Empty) throw new ArgumentException("id required", nameof(id));
        if (tenantId == Guid.Empty) throw new ArgumentException("tenantId required", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(externalId)) throw new ArgumentException("externalId required", nameof(externalId));

        Id = id;
        TenantId = tenantId;
        ExternalId = externalId;
        Email = email;
        PhoneE164 = phoneE164;
        PushToken = pushToken;
        WebhookUrl = webhookUrl;
        Locale = locale;
        TimeZoneId = timeZoneId;
        QuietHoursStart = quietHoursStart;
        QuietHoursEnd = quietHoursEnd;
        FirstName = firstName;
        LastName = lastName;
    }

    public bool HasChannel(NotificationChannel channel) => channel switch
    {
        NotificationChannel.Email => !string.IsNullOrWhiteSpace(Email),
        NotificationChannel.Sms => !string.IsNullOrWhiteSpace(PhoneE164),
        NotificationChannel.Push => !string.IsNullOrWhiteSpace(PushToken),
        NotificationChannel.Webhook => !string.IsNullOrWhiteSpace(WebhookUrl),
        _ => false,
    };

    public string? AddressFor(NotificationChannel channel) => channel switch
    {
        NotificationChannel.Email => Email,
        NotificationChannel.Sms => PhoneE164,
        NotificationChannel.Push => PushToken,
        NotificationChannel.Webhook => WebhookUrl,
        _ => null,
    };

    public bool HasQuietHours() => QuietHoursStart != QuietHoursEnd;
}
