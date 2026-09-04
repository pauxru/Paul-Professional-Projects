using Collab.Domain.Abstractions;

namespace Collab.Domain.Notifications;

public enum NotificationType
{
    Mention = 0,
    CommentReply = 1,
    DocumentDigest = 2,
    CommentResolved = 3
}

/// <summary>
/// A per-user inbox item. Created on mentions and document activity; delivered live over SignalR
/// when the recipient is connected and always persisted so it survives an offline period.
/// </summary>
public sealed class Notification
{
    private Notification() { }

    public Notification(
        Guid userId,
        NotificationType type,
        string message,
        IClock clock,
        Guid? documentId = null,
        Guid? commentId = null,
        Guid? actorUserId = null)
    {
        Id = Guid.NewGuid();
        UserId = userId;
        Type = type;
        Message = message;
        DocumentId = documentId;
        CommentId = commentId;
        ActorUserId = actorUserId;
        IsRead = false;
        CreatedAt = clock.UtcNow;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public NotificationType Type { get; private set; }
    public string Message { get; private set; } = null!;
    public Guid? DocumentId { get; private set; }
    public Guid? CommentId { get; private set; }
    public Guid? ActorUserId { get; private set; }
    public bool IsRead { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ReadAt { get; private set; }

    public void MarkRead(IClock clock)
    {
        if (IsRead) return;
        IsRead = true;
        ReadAt = clock.UtcNow;
    }
}
