using Collab.Application.Contracts;

namespace Collab.Application.Abstractions;

/// <summary>
/// In-memory presence registry (per-document participant lists). Deliberately not persisted: on a
/// hub restart the registry is empty and clients re-announce via their next heartbeat. Presence
/// changes are coalesced — callers mutate freely and a background flusher drains <see cref="DrainDirty"/>
/// at a fixed cadence so a burst of cursor moves becomes a single broadcast.
/// </summary>
public interface IPresenceStore
{
    PresenceDto Join(Guid documentId, string connectionId, Guid userId, string userName, DateTimeOffset now);

    /// <summary>Remove a single (document, connection) presence. Returns true if it existed.</summary>
    bool LeaveDocument(Guid documentId, string connectionId);

    /// <summary>Remove all presence for a connection (on disconnect). Returns the documents affected.</summary>
    IReadOnlyList<Guid> Disconnect(string connectionId);

    PresenceDto? Update(Guid documentId, string connectionId, PresenceUpdate update, DateTimeOffset now);

    void Heartbeat(string connectionId, DateTimeOffset now);

    IReadOnlyList<PresenceDto> Participants(Guid documentId);

    IReadOnlyList<Guid> DocumentsOf(string connectionId);

    /// <summary>Mark stale connections idle and evict expired ones. Returns affected documents.</summary>
    IReadOnlyList<Guid> ApplyLiveness(DateTimeOffset idleBefore, DateTimeOffset evictBefore);

    /// <summary>Return and clear the set of documents whose presence changed since the last drain.</summary>
    IReadOnlyList<Guid> DrainDirty();
}
