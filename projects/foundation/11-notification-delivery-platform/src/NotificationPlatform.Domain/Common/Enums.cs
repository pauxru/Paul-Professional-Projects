namespace NotificationPlatform.Domain.Common;

public enum NotificationChannel
{
    Email = 1,
    Sms = 2,
    Push = 3,
    Webhook = 4,
}

public enum NotificationPriority
{
    Marketing = 1,
    Transactional = 2,
}

public enum NotificationCategory
{
    Transactional = 1,
    Marketing = 2,
    System = 3,
}

public enum NotificationStatus
{
    Queued = 1,
    Scheduled = 2,
    Rendering = 3,
    Dispatched = 4,
    Sent = 5,
    Delivered = 6,
    Bounced = 7,
    Failed = 8,
    Suppressed = 9,
    DeadLettered = 10,
}

public enum ProviderResultKind
{
    Success = 1,
    TransientFailure = 2,
    PermanentFailure = 3,
    Throttled = 4,
}

public enum CircuitState
{
    Closed = 1,
    Open = 2,
    HalfOpen = 3,
}

public enum SuppressionReason
{
    HardBounce = 1,
    Complaint = 2,
    Unsubscribe = 3,
    ManualBlock = 4,
}
