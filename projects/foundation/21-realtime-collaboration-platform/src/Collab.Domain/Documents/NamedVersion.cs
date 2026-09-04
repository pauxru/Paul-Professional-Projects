using Collab.Domain.Abstractions;

namespace Collab.Domain.Documents;

/// <summary>A human-named checkpoint of a document at a specific sequence (e.g. "v1.0 sign-off").</summary>
public sealed class NamedVersion
{
    private NamedVersion() { }

    public NamedVersion(Guid documentId, string name, long atSequence, Guid createdByUserId, IClock clock, Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        DocumentId = documentId;
        Name = name;
        AtSequence = atSequence;
        CreatedByUserId = createdByUserId;
        CreatedAt = clock.UtcNow;
    }

    public Guid Id { get; private set; }
    public Guid DocumentId { get; private set; }
    public string Name { get; private set; } = null!;
    public long AtSequence { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
