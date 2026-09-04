using Collab.Application.Abstractions;
using Collab.Application.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace Collab.Api.Realtime;

/// <summary>
/// Adapter that lets the Application layer push to clients without referencing SignalR. Notifications
/// go to a specific user's connections (delivered live; they were already persisted, so an offline
/// user gets them from their inbox on return). Comment events go to the document group.
/// </summary>
public sealed class SignalRClientNotifier(IHubContext<CollaborationHub> hub) : IClientNotifier
{
    public Task NotifyUserAsync(Guid userId, NotificationDto notification, CancellationToken ct = default) =>
        hub.Clients.User(userId.ToString()).SendAsync("NotificationReceived", notification, ct);

    public Task CommentAddedAsync(Guid documentId, CommentDto comment, CancellationToken ct = default) =>
        hub.Clients.Group(HubGroups.ForDocument(documentId)).SendAsync("CommentAdded", comment, ct);
}
