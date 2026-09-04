using Collab.Application.Abstractions;
using Collab.Application.Contracts;
using Collab.Domain.Abstractions;
using Collab.Domain.Notifications;

namespace Collab.Application.Services;

/// <summary>Per-user notification inbox (read/unread). Delivery of new items happens elsewhere; this
/// service owns the persisted inbox that survives an offline period.</summary>
public sealed class NotificationService(
    INotificationRepository notifications,
    IClock clock,
    IUnitOfWork uow)
{
    public async Task<PagedResult<NotificationDto>> ListAsync(Guid userId, bool unreadOnly, int page, int pageSize, CancellationToken ct = default)
    {
        page = page < 1 ? 1 : page;
        pageSize = pageSize is < 1 or > 200 ? 50 : pageSize;
        var total = await notifications.CountForUserAsync(userId, unreadOnly, ct);
        var items = await notifications.ListForUserAsync(userId, unreadOnly, (page - 1) * pageSize, pageSize, ct);
        return new PagedResult<NotificationDto>(items.Select(Map).ToArray(), page, pageSize, total);
    }

    public async Task<NotificationDto> MarkReadAsync(Guid userId, Guid notificationId, CancellationToken ct = default)
    {
        var notification = await notifications.GetAsync(notificationId, ct);
        if (notification is null || notification.UserId != userId)
            throw new NotFoundException("Notification not found.");
        notification.MarkRead(clock);
        await uow.SaveChangesAsync(ct);
        return Map(notification);
    }

    public async Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct = default)
    {
        var unread = await notifications.ListForUserAsync(userId, unreadOnly: true, 0, int.MaxValue, ct);
        foreach (var notification in unread)
            notification.MarkRead(clock);
        await uow.SaveChangesAsync(ct);
        return unread.Count;
    }

    private static NotificationDto Map(Notification n) =>
        new(n.Id, n.Type.ToString(), n.Message, n.DocumentId, n.CommentId, n.IsRead, n.CreatedAt);
}
