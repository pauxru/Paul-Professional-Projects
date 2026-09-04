using Collab.Application.Abstractions;
using Collab.Application.Contracts;

namespace Collab.Infrastructure.Realtime;

/// <summary>
/// Process-local presence registry. Deliberately NOT persisted: on a hub/process restart it starts
/// empty and clients re-announce on their next heartbeat (documented behaviour). All access is under
/// one lock — presence operations are tiny and this keeps the coalescing dirty-set correct.
/// </summary>
public sealed class InMemoryPresenceStore : IPresenceStore
{
    private sealed class Entry
    {
        public required Guid DocumentId { get; init; }
        public required string ConnectionId { get; init; }
        public required Guid UserId { get; init; }
        public required string UserName { get; set; }
        public int CursorStart { get; set; }
        public int CursorEnd { get; set; }
        public PresenceStatus Status { get; set; }
        public DateTimeOffset LastSeen { get; set; }

        public PresenceDto ToDto() =>
            new(DocumentId, ConnectionId, UserId, UserName, CursorStart, CursorEnd, Status);
    }

    private readonly object _gate = new();
    private readonly Dictionary<(Guid Document, string Connection), Entry> _entries = [];
    private readonly HashSet<Guid> _dirty = [];

    public PresenceDto Join(Guid documentId, string connectionId, Guid userId, string userName, DateTimeOffset now)
    {
        lock (_gate)
        {
            var entry = new Entry
            {
                DocumentId = documentId,
                ConnectionId = connectionId,
                UserId = userId,
                UserName = userName,
                Status = PresenceStatus.Active,
                LastSeen = now
            };
            _entries[(documentId, connectionId)] = entry;
            _dirty.Add(documentId);
            return entry.ToDto();
        }
    }

    public bool LeaveDocument(Guid documentId, string connectionId)
    {
        lock (_gate)
        {
            if (!_entries.Remove((documentId, connectionId))) return false;
            _dirty.Add(documentId);
            return true;
        }
    }

    public IReadOnlyList<Guid> Disconnect(string connectionId)
    {
        lock (_gate)
        {
            var affected = _entries.Keys.Where(k => k.Connection == connectionId).Select(k => k.Document).Distinct().ToList();
            foreach (var documentId in affected)
            {
                _entries.Remove((documentId, connectionId));
                _dirty.Add(documentId);
            }
            return affected;
        }
    }

    public PresenceDto? Update(Guid documentId, string connectionId, PresenceUpdate update, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue((documentId, connectionId), out var entry)) return null;
            entry.CursorStart = update.CursorStart;
            entry.CursorEnd = update.CursorEnd;
            entry.Status = update.Status;
            entry.LastSeen = now;
            _dirty.Add(documentId);
            return entry.ToDto();
        }
    }

    public void Heartbeat(string connectionId, DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var kvp in _entries)
            {
                if (kvp.Key.Connection != connectionId) continue;
                var entry = kvp.Value;
                if (entry.Status == PresenceStatus.Idle)
                {
                    entry.Status = PresenceStatus.Active;
                    _dirty.Add(entry.DocumentId);
                }
                entry.LastSeen = now;
            }
        }
    }

    public IReadOnlyList<PresenceDto> Participants(Guid documentId)
    {
        lock (_gate)
        {
            return _entries.Values
                .Where(e => e.DocumentId == documentId)
                .OrderBy(e => e.UserName, StringComparer.Ordinal)
                .Select(e => e.ToDto())
                .ToList();
        }
    }

    public IReadOnlyList<Guid> DocumentsOf(string connectionId)
    {
        lock (_gate)
        {
            return _entries.Keys.Where(k => k.Connection == connectionId).Select(k => k.Document).Distinct().ToList();
        }
    }

    public IReadOnlyList<Guid> ApplyLiveness(DateTimeOffset idleBefore, DateTimeOffset evictBefore)
    {
        lock (_gate)
        {
            var affected = new HashSet<Guid>();
            foreach (var kvp in _entries.ToList())
            {
                var entry = kvp.Value;
                if (entry.LastSeen < evictBefore)
                {
                    _entries.Remove(kvp.Key);
                    affected.Add(entry.DocumentId);
                }
                else if (entry.LastSeen < idleBefore && entry.Status == PresenceStatus.Active)
                {
                    entry.Status = PresenceStatus.Idle;
                    affected.Add(entry.DocumentId);
                }
            }
            foreach (var documentId in affected) _dirty.Add(documentId);
            return affected.ToList();
        }
    }

    public IReadOnlyList<Guid> DrainDirty()
    {
        lock (_gate)
        {
            if (_dirty.Count == 0) return [];
            var drained = _dirty.ToList();
            _dirty.Clear();
            return drained;
        }
    }
}
