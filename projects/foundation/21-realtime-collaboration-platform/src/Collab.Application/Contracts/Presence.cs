namespace Collab.Application.Contracts;

public enum PresenceStatus
{
    Active = 0,
    Idle = 1
}

/// <summary>
/// One participant's presence in a document: their identity, cursor/selection range and liveness.
/// Cursor positions are character offsets in the rendered text.
/// </summary>
public sealed record PresenceDto(
    Guid DocumentId,
    string ConnectionId,
    Guid UserId,
    string UserName,
    int CursorStart,
    int CursorEnd,
    PresenceStatus Status);

/// <summary>The full set of participants in a document, sent on join and on change.</summary>
public sealed record PresenceSnapshot(Guid DocumentId, IReadOnlyList<PresenceDto> Participants);

/// <summary>A client's presence update (cursor move / status change / heartbeat).</summary>
public sealed record PresenceUpdate(int CursorStart, int CursorEnd, PresenceStatus Status);
