using Collab.Domain.Abstractions;

namespace Collab.Domain.Documents;

/// <summary>
/// A periodic checkpoint of a document's full CRDT state at a given sequence, so a document can be
/// loaded quickly (latest snapshot + replay of the tail) instead of replaying the entire log.
/// </summary>
public sealed class DocumentSnapshot
{
    private DocumentSnapshot() { }

    public DocumentSnapshot(
        Guid documentId,
        long atSequence,
        string state,
        string materializedContent,
        IClock clock,
        Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        DocumentId = documentId;
        AtSequence = atSequence;
        State = state;
        MaterializedContent = materializedContent;
        CreatedAt = clock.UtcNow;
    }

    public Guid Id { get; private set; }
    public Guid DocumentId { get; private set; }
    public long AtSequence { get; private set; }

    /// <summary>Serialized CRDT state (RGA node set or structured document state).</summary>
    public string State { get; private set; } = null!;

    /// <summary>Rendered content at the checkpoint (for cheap previews and integrity checks).</summary>
    public string MaterializedContent { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
}
