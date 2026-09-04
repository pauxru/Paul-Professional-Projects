namespace NotificationPlatform.Application.Notifications;

using System.Text.Json;
using NotificationPlatform.Domain.Common;

public sealed record SendNotificationRequest(
    string TemplateKey,
    NotificationChannel Channel,
    string RecipientExternalId,
    JsonElement? Payload,
    NotificationPriority Priority,
    NotificationCategory Category,
    string? Locale,
    DateTimeOffset? SendAt,
    string? IdempotencyKey,
    string? DeduplicationKey);

public sealed record BulkSendItem(
    string TemplateKey,
    NotificationChannel Channel,
    string RecipientExternalId,
    JsonElement? Payload,
    NotificationPriority Priority,
    NotificationCategory Category,
    string? Locale,
    DateTimeOffset? SendAt,
    string? DeduplicationKey);

public sealed record BulkSendRequest(string? IdempotencyKey, IReadOnlyList<BulkSendItem> Items);

public enum SendOutcomeKind
{
    Accepted = 1,
    Deduplicated = 2,
    Suppressed = 3,
    Rejected = 4,
    IdempotentReplay = 5,
}

public sealed record SendOutcome(
    SendOutcomeKind Kind,
    Guid? NotificationId,
    string? Message,
    NotificationStatus? Status);

public sealed record BulkSendOutcome(IReadOnlyList<SendOutcome> Results);

public sealed record NotificationDto(
    Guid Id,
    Guid TenantId,
    Guid RecipientId,
    string TemplateKey,
    NotificationChannel Channel,
    NotificationCategory Category,
    NotificationPriority Priority,
    NotificationStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ScheduledFor,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? FailedAt,
    int AttemptCount,
    string? LastProvider,
    string? LastError,
    string? Address,
    string? CorrelationId);
