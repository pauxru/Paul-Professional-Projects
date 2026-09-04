namespace NotificationPlatform.Domain.Notifications;

using NotificationPlatform.Domain.Common;

public sealed class Notification
{
    private readonly List<NotificationAttempt> _attempts = new();

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid RecipientId { get; private set; }
    public string TemplateKey { get; private set; } = string.Empty;
    public NotificationChannel Channel { get; private set; }
    public NotificationCategory Category { get; private set; }
    public NotificationPriority Priority { get; private set; }
    public NotificationStatus Status { get; private set; }
    public string? IdempotencyKey { get; private set; }
    public string? DeduplicationKey { get; private set; }
    public string PayloadJson { get; private set; } = "{}";
    public string Locale { get; private set; } = "en";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ScheduledFor { get; private set; }
    public DateTimeOffset? DispatchedAt { get; private set; }
    public DateTimeOffset? DeliveredAt { get; private set; }
    public DateTimeOffset? FailedAt { get; private set; }
    public string? Address { get; private set; }
    public string? RenderedSubject { get; private set; }
    public string? RenderedBody { get; private set; }
    public string? LastProvider { get; private set; }
    public string? LastError { get; private set; }
    public int AttemptCount { get; private set; }
    public int MaxAttempts { get; private set; } = 5;
    public int? TemplateVersion { get; private set; }
    public string? CorrelationId { get; private set; }

    public IReadOnlyCollection<NotificationAttempt> Attempts => _attempts;

    private Notification() { }

    public Notification(
        Guid id,
        Guid tenantId,
        Guid recipientId,
        string templateKey,
        NotificationChannel channel,
        NotificationCategory category,
        NotificationPriority priority,
        string payloadJson,
        string locale,
        string? idempotencyKey,
        string? deduplicationKey,
        DateTimeOffset createdAt,
        DateTimeOffset? scheduledFor,
        int maxAttempts,
        string? correlationId)
    {
        if (id == Guid.Empty) throw new ArgumentException("id required", nameof(id));
        if (tenantId == Guid.Empty) throw new ArgumentException("tenantId required", nameof(tenantId));
        if (recipientId == Guid.Empty) throw new ArgumentException("recipientId required", nameof(recipientId));
        if (string.IsNullOrWhiteSpace(templateKey)) throw new ArgumentException("templateKey required", nameof(templateKey));
        if (maxAttempts <= 0 || maxAttempts > 20) throw new ArgumentException("maxAttempts must be 1..20", nameof(maxAttempts));

        Id = id;
        TenantId = tenantId;
        RecipientId = recipientId;
        TemplateKey = templateKey;
        Channel = channel;
        Category = category;
        Priority = priority;
        PayloadJson = payloadJson;
        Locale = locale;
        IdempotencyKey = idempotencyKey;
        DeduplicationKey = deduplicationKey;
        CreatedAt = createdAt;
        ScheduledFor = scheduledFor;
        MaxAttempts = maxAttempts;
        CorrelationId = correlationId;
        Status = scheduledFor.HasValue ? NotificationStatus.Scheduled : NotificationStatus.Queued;
    }

    public void MarkScheduled(DateTimeOffset at)
    {
        ScheduledFor = at;
        Status = NotificationStatus.Scheduled;
    }

    public void BeginRendering()
    {
        Require(NotificationStatus.Queued, NotificationStatus.Scheduled);
        Status = NotificationStatus.Rendering;
    }

    public void CompleteRendering(int templateVersion, string? subject, string body, string? address)
    {
        Require(NotificationStatus.Rendering);
        TemplateVersion = templateVersion;
        RenderedSubject = subject;
        RenderedBody = body;
        Address = address;
        Status = NotificationStatus.Dispatched;
    }

    public void Suppress(string reason)
    {
        LastError = reason;
        Status = NotificationStatus.Suppressed;
    }

    public void RecordAttempt(NotificationAttempt attempt)
    {
        _attempts.Add(attempt);
        AttemptCount++;
        LastProvider = attempt.ProviderName;
        LastError = attempt.Error;
    }

    public void MarkSent(DateTimeOffset at)
    {
        Status = NotificationStatus.Sent;
        DispatchedAt = at;
    }

    public void MarkDelivered(DateTimeOffset at)
    {
        Status = NotificationStatus.Delivered;
        DeliveredAt = at;
    }

    public void MarkBounced(string reason, DateTimeOffset at)
    {
        Status = NotificationStatus.Bounced;
        LastError = reason;
        FailedAt = at;
    }

    public void MarkFailed(string reason, DateTimeOffset at)
    {
        Status = NotificationStatus.Failed;
        LastError = reason;
        FailedAt = at;
    }

    public void MarkDeadLettered(string reason, DateTimeOffset at)
    {
        Status = NotificationStatus.DeadLettered;
        LastError = reason;
        FailedAt = at;
    }

    public void MarkQueued()
    {
        Status = NotificationStatus.Queued;
    }

    public bool HasMoreAttempts() => AttemptCount < MaxAttempts;

    public bool CanBypassQuietHours() => Priority == NotificationPriority.Transactional;

    private void Require(params NotificationStatus[] allowed)
    {
        if (!allowed.Contains(Status))
            throw new DomainException($"Notification {Id} is in status {Status}, expected one of {string.Join(',', allowed)}");
    }
}

public sealed class NotificationAttempt
{
    public Guid Id { get; private set; }
    public Guid NotificationId { get; private set; }
    public int AttemptNumber { get; private set; }
    public string ProviderName { get; private set; } = string.Empty;
    public ProviderResultKind Result { get; private set; }
    public string? Error { get; private set; }
    public int LatencyMilliseconds { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public string? ProviderMessageId { get; private set; }

    private NotificationAttempt() { }

    public NotificationAttempt(Guid id, Guid notificationId, int attemptNumber, string providerName, ProviderResultKind result, string? error, int latencyMs, DateTimeOffset occurredAt, string? providerMessageId)
    {
        Id = id;
        NotificationId = notificationId;
        AttemptNumber = attemptNumber;
        ProviderName = providerName;
        Result = result;
        Error = error;
        LatencyMilliseconds = latencyMs;
        OccurredAt = occurredAt;
        ProviderMessageId = providerMessageId;
    }
}

public sealed class IdempotencyRecord
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Key { get; private set; } = string.Empty;
    public string RequestHash { get; private set; } = string.Empty;
    public string ResponseJson { get; private set; } = string.Empty;
    public int StatusCode { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private IdempotencyRecord() { }

    public IdempotencyRecord(Guid id, Guid tenantId, string key, string requestHash, string responseJson, int statusCode, DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        Key = key;
        RequestHash = requestHash;
        ResponseJson = responseJson;
        StatusCode = statusCode;
        CreatedAt = createdAt;
    }
}

public sealed class DeliveryReceipt
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid NotificationId { get; private set; }
    public string ProviderName { get; private set; } = string.Empty;
    public string ProviderMessageId { get; private set; } = string.Empty;
    public string Kind { get; private set; } = string.Empty; // delivered/bounced/complained
    public string? Reason { get; private set; }
    public string Nonce { get; private set; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; private set; }

    private DeliveryReceipt() { }

    public DeliveryReceipt(Guid id, Guid tenantId, Guid notificationId, string providerName, string providerMessageId, string kind, string? reason, string nonce, DateTimeOffset receivedAt)
    {
        Id = id;
        TenantId = tenantId;
        NotificationId = notificationId;
        ProviderName = providerName;
        ProviderMessageId = providerMessageId;
        Kind = kind;
        Reason = reason;
        Nonce = nonce;
        ReceivedAt = receivedAt;
    }
}
