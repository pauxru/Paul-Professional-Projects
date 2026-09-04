namespace NotificationPlatform.Domain.Templates;

using NotificationPlatform.Domain.Common;

public sealed class NotificationTemplate
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string TemplateKey { get; private set; } = string.Empty;
    public NotificationChannel Channel { get; private set; }
    public string Locale { get; private set; } = "en";
    public int Version { get; private set; }
    public string? Subject { get; private set; }
    public string Body { get; private set; } = string.Empty;
    public NotificationCategory Category { get; private set; }
    public bool StrictMode { get; private set; } = true;
    public bool IsActive { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private set; }

    private NotificationTemplate() { }

    public NotificationTemplate(
        Guid id,
        Guid tenantId,
        string templateKey,
        NotificationChannel channel,
        string locale,
        int version,
        string? subject,
        string body,
        NotificationCategory category,
        bool strictMode,
        bool isActive,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty) throw new ArgumentException("id required", nameof(id));
        if (tenantId == Guid.Empty) throw new ArgumentException("tenantId required", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(templateKey)) throw new ArgumentException("templateKey required", nameof(templateKey));
        if (string.IsNullOrWhiteSpace(locale)) throw new ArgumentException("locale required", nameof(locale));
        if (version <= 0) throw new ArgumentException("version must be positive", nameof(version));
        if (string.IsNullOrEmpty(body)) throw new ArgumentException("body required", nameof(body));
        if (channel == NotificationChannel.Email && string.IsNullOrWhiteSpace(subject))
            throw new DomainException("Email templates require a subject.");

        Id = id;
        TenantId = tenantId;
        TemplateKey = templateKey;
        Channel = channel;
        Locale = locale;
        Version = version;
        Subject = subject;
        Body = body;
        Category = category;
        StrictMode = strictMode;
        IsActive = isActive;
        CreatedAt = createdAt;
    }

    public void Deactivate() => IsActive = false;
}
