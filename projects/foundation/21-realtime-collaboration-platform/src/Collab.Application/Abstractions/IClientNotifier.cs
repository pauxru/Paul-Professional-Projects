using Collab.Application.Contracts;

namespace Collab.Application.Abstractions;

/// <summary>
/// Server-to-client push that the Application layer uses without referencing SignalR. Implemented
/// in the API host over the hub's <c>IHubContext</c>. Notifications are delivered live when the user
/// is connected; they are always persisted first so nothing is lost when they are offline.
/// </summary>
public interface IClientNotifier
{
    Task NotifyUserAsync(Guid userId, NotificationDto notification, CancellationToken ct = default);
    Task CommentAddedAsync(Guid documentId, CommentDto comment, CancellationToken ct = default);
}

/// <summary>
/// Custom telemetry sink for collaboration-specific metrics (connected clients, ops/sec, merge
/// duration, resync count, rejected ops). Implemented in Infrastructure over a <c>Meter</c>.
/// </summary>
public interface ICollabMetrics
{
    void RecordApply(int operationCount, double milliseconds);
    void RecordRejected();
    void RecordResync();
    void ClientConnected();
    void ClientDisconnected();
}

/// <summary>Append-only audit trail writer. Records are added to the current unit of work.</summary>
public interface IAuditLog
{
    void Record(
        string action,
        string resourceType,
        string resourceId,
        Guid? actorUserId = null,
        Guid? workspaceId = null,
        Guid? documentId = null,
        string? details = null,
        string? correlationId = null);
}
