using Microsoft.AspNetCore.SignalR;

namespace Collab.Api.Auth;

/// <summary>
/// Maps a SignalR connection to a stable user id (the <c>sub</c> claim) so the server can push
/// notifications to a specific user across all their connections via <c>Clients.User(id)</c>.
/// </summary>
public sealed class SubjectUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) =>
        connection.User?.FindFirst("sub")?.Value;
}
