using Collab.Application.Abstractions;
using Collab.Application.Contracts;
using Collab.Domain.Abstractions;
using Collab.Domain.Authorization;
using Collab.Domain.Comments;
using Collab.Domain.Notifications;

namespace Collab.Application.Services;

/// <summary>
/// Comments and threads anchored to a text range or a structured field, with mention notifications
/// and resolve/reopen. Anchor rebasing itself happens in the collaboration engine as text mutates;
/// this service owns creation, threading and lifecycle.
/// </summary>
public sealed class CommentService(
    ICommentRepository comments,
    IDocumentRepository documents,
    INotificationRepository notifications,
    IAccessControl access,
    IClientNotifier notifier,
    IAuditLog audit,
    IClock clock,
    IUnitOfWork uow)
{
    public async Task<CommentDto> CreateAsync(Guid userId, Guid documentId, CreateCommentRequest request, CancellationToken ct = default)
    {
        var document = await documents.GetAsync(documentId, ct)
            ?? throw new NotFoundException("Document not found.");
        await AuthorizeAsync(userId, document.WorkspaceId, Capability.Comment, ct);

        var body = (request.Body ?? string.Empty).Trim();
        if (body.Length is 0 or > 10_000)
            throw new ValidationAppException(nameof(request.Body), "Body must be between 1 and 10000 characters.");

        var mentions = request.Mentions ?? [];
        Comment comment;
        if (!string.IsNullOrWhiteSpace(request.FieldPath))
        {
            comment = Comment.CreateFieldComment(documentId, userId, body, request.FieldPath!, mentions, clock);
        }
        else
        {
            var start = request.AnchorStart ?? 0;
            var end = request.AnchorEnd ?? start;
            if (start < 0 || end < start)
                throw new ValidationAppException("anchor", "Anchor range is invalid.");
            comment = Comment.CreateTextComment(documentId, userId, body, start, end, mentions, clock);
        }

        comments.Add(comment);
        var created = QueueMentionNotifications(userId, documentId, comment, NotificationType.Mention);
        audit.Record("comment.created", "comment", comment.Id.ToString(), userId, document.WorkspaceId, documentId);
        await uow.SaveChangesAsync(ct);

        var dto = Map(comment);
        await notifier.CommentAddedAsync(documentId, dto, ct);
        await PushAsync(created, ct);
        return dto;
    }

    public async Task<CommentDto> ReplyAsync(Guid userId, Guid commentId, ReplyRequest request, CancellationToken ct = default)
    {
        var parent = await comments.GetAsync(commentId, ct)
            ?? throw new NotFoundException("Comment not found.");
        var document = await documents.GetAsync(parent.DocumentId, ct)
            ?? throw new NotFoundException("Document not found.");
        await AuthorizeAsync(userId, document.WorkspaceId, Capability.Comment, ct);

        var body = (request.Body ?? string.Empty).Trim();
        if (body.Length is 0 or > 10_000)
            throw new ValidationAppException(nameof(request.Body), "Body must be between 1 and 10000 characters.");

        var reply = parent.Reply(userId, body, request.Mentions ?? [], clock);
        comments.Add(reply);

        var created = QueueMentionNotifications(userId, parent.DocumentId, reply, NotificationType.Mention);
        if (parent.AuthorUserId != userId && !created.Any(n => n.UserId == parent.AuthorUserId))
        {
            var replyNotification = new Notification(parent.AuthorUserId, NotificationType.CommentReply,
                "You have a new reply to your comment.", clock, parent.DocumentId, reply.Id, userId);
            notifications.Add(replyNotification);
            created.Add(replyNotification);
        }

        audit.Record("comment.replied", "comment", reply.Id.ToString(), userId, document.WorkspaceId, parent.DocumentId);
        await uow.SaveChangesAsync(ct);

        var dto = Map(reply);
        await notifier.CommentAddedAsync(parent.DocumentId, dto, ct);
        await PushAsync(created, ct);
        return dto;
    }

    public async Task<CommentDto> ResolveAsync(Guid userId, Guid commentId, CancellationToken ct = default)
    {
        var comment = await comments.GetAsync(commentId, ct)
            ?? throw new NotFoundException("Comment not found.");
        var document = await documents.GetAsync(comment.DocumentId, ct)
            ?? throw new NotFoundException("Document not found.");
        await AuthorizeAsync(userId, document.WorkspaceId, Capability.Comment, ct);

        comment.Resolve(userId, clock);
        var created = new List<Notification>();
        if (comment.AuthorUserId != userId)
        {
            var notification = new Notification(comment.AuthorUserId, NotificationType.CommentResolved,
                "Your comment was resolved.", clock, comment.DocumentId, comment.Id, userId);
            notifications.Add(notification);
            created.Add(notification);
        }
        audit.Record("comment.resolved", "comment", comment.Id.ToString(), userId, document.WorkspaceId, comment.DocumentId);
        await uow.SaveChangesAsync(ct);

        await PushAsync(created, ct);
        return Map(comment);
    }

    public async Task<CommentDto> ReopenAsync(Guid userId, Guid commentId, CancellationToken ct = default)
    {
        var comment = await comments.GetAsync(commentId, ct)
            ?? throw new NotFoundException("Comment not found.");
        var document = await documents.GetAsync(comment.DocumentId, ct)
            ?? throw new NotFoundException("Document not found.");
        await AuthorizeAsync(userId, document.WorkspaceId, Capability.Comment, ct);

        comment.Reopen(clock);
        audit.Record("comment.reopened", "comment", comment.Id.ToString(), userId, document.WorkspaceId, comment.DocumentId);
        await uow.SaveChangesAsync(ct);
        return Map(comment);
    }

    public async Task<IReadOnlyList<CommentDto>> ListByDocumentAsync(Guid userId, Guid documentId, CancellationToken ct = default)
    {
        var document = await documents.GetAsync(documentId, ct)
            ?? throw new NotFoundException("Document not found.");
        await AuthorizeAsync(userId, document.WorkspaceId, Capability.View, ct);
        var list = await comments.ListByDocumentAsync(documentId, ct);
        return list.Select(Map).ToArray();
    }

    private List<Notification> QueueMentionNotifications(Guid actorUserId, Guid documentId, Comment comment, NotificationType type)
    {
        var created = new List<Notification>();
        foreach (var mentioned in comment.Mentions.Where(m => m != actorUserId).Distinct())
        {
            var notification = new Notification(mentioned, type,
                "You were mentioned in a comment.", clock, documentId, comment.Id, actorUserId);
            notifications.Add(notification);
            created.Add(notification);
        }
        return created;
    }

    private async Task PushAsync(IEnumerable<Notification> created, CancellationToken ct)
    {
        foreach (var notification in created)
            await notifier.NotifyUserAsync(notification.UserId, MapNotification(notification), ct);
    }

    private async Task AuthorizeAsync(Guid userId, Guid workspaceId, Capability capability, CancellationToken ct)
    {
        var decision = await access.ForWorkspaceAsync(userId, workspaceId, capability, ct);
        if (!decision.Allowed) throw new ForbiddenException(decision.Reason);
    }

    private static CommentDto Map(Comment c) => new(
        c.Id, c.DocumentId, c.ThreadId, c.ParentCommentId, c.AuthorUserId, c.Body,
        c.AnchorKind.ToString(), c.AnchorStart, c.AnchorEnd, c.FieldPath, c.IsOrphaned,
        c.Status.ToString(), c.Mentions, c.CreatedAt);

    private static NotificationDto MapNotification(Notification n) =>
        new(n.Id, n.Type.ToString(), n.Message, n.DocumentId, n.CommentId, n.IsRead, n.CreatedAt);
}
