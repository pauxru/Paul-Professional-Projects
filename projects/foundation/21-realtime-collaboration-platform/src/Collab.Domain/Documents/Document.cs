using Collab.Domain.Abstractions;

namespace Collab.Domain.Documents;

/// <summary>
/// The conflict strategy a document uses. Text uses the RGA CRDT; Structured uses field-level LWW.
/// </summary>
public enum DocumentType
{
    Text = 0,
    Structured = 1
}

/// <summary>
/// A collaboratively-edited document. Its content is NOT stored on this row; it is the fold of the
/// operation log over the latest snapshot. <see cref="CurrentSequence"/> is the authoritative
/// server version — the sequence number of the last operation the server accepted.
/// </summary>
public sealed class Document
{
    private Document() { }

    public Document(Guid workspaceId, string title, DocumentType type, Guid createdByUserId, IClock clock, Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        WorkspaceId = workspaceId;
        Title = title;
        Type = type;
        CreatedByUserId = createdByUserId;
        CurrentSequence = 0;
        CreatedAt = UpdatedAt = clock.UtcNow;
    }

    public Guid Id { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public string Title { get; private set; } = null!;
    public DocumentType Type { get; private set; }
    public Guid CreatedByUserId { get; private set; }

    /// <summary>Server-authoritative version: the sequence number of the last accepted operation.</summary>
    public long CurrentSequence { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public void Rename(string title, IClock clock)
    {
        Title = title;
        UpdatedAt = clock.UtcNow;
    }

    /// <summary>Advance the authoritative version after accepting operations up to <paramref name="sequence"/>.</summary>
    public void AdvanceTo(long sequence, IClock clock)
    {
        if (sequence <= CurrentSequence) return;
        CurrentSequence = sequence;
        UpdatedAt = clock.UtcNow;
    }
}
