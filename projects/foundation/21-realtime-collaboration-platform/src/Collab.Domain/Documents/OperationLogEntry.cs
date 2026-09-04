using Collab.Domain.Abstractions;

namespace Collab.Domain.Documents;

/// <summary>
/// An append-only entry in a document's operation log. Each entry is one accepted change set
/// (one or more primitive CRDT operations serialized to <see cref="Payload"/>) stamped with a
/// monotonic, gap-free-per-document <see cref="ServerSequence"/>. The log is the source of truth;
/// replaying it (optionally from a snapshot) reproduces the document at any point in time.
/// </summary>
public sealed class OperationLogEntry
{
    private OperationLogEntry() { }

    public OperationLogEntry(
        Guid documentId,
        long serverSequence,
        Guid authorUserId,
        string authorReplicaId,
        DocumentType kind,
        string payload,
        IClock clock,
        Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        DocumentId = documentId;
        ServerSequence = serverSequence;
        AuthorUserId = authorUserId;
        AuthorReplicaId = authorReplicaId;
        Kind = kind;
        Payload = payload;
        CreatedAt = clock.UtcNow;
    }

    public Guid Id { get; private set; }
    public Guid DocumentId { get; private set; }
    public long ServerSequence { get; private set; }
    public Guid AuthorUserId { get; private set; }
    public string AuthorReplicaId { get; private set; } = null!;
    public DocumentType Kind { get; private set; }
    public string Payload { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
}
