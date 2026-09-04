using Collab.Domain.Crdt;
using Collab.Domain.Documents;
using Collab.Domain.Structured;

namespace Collab.Application.Abstractions;

/// <summary>
/// The authoritative in-memory state of an actively-edited document plus a gate that serializes
/// mutations so server sequence numbers are assigned monotonically under concurrent submits.
/// One of <see cref="Text"/> / <see cref="Structured"/> is populated depending on the document type.
/// </summary>
public sealed class DocumentRuntime
{
    public DocumentRuntime(Guid documentId, DocumentType type, RgaDocument? text, StructuredDocument? structured, long sequence)
    {
        DocumentId = documentId;
        Type = type;
        Text = text;
        Structured = structured;
        Sequence = sequence;
        Clock = new LamportClock();
    }

    public Guid DocumentId { get; }
    public DocumentType Type { get; }
    public RgaDocument? Text { get; }
    public StructuredDocument? Structured { get; }
    public long Sequence { get; set; }
    public long OpsSinceSnapshot { get; set; }

    /// <summary>Server-side Lamport clock, kept ahead of every operation the server has accepted.</summary>
    public LamportClock Clock { get; }

    /// <summary>Serializes all mutation of this runtime — the source of monotonic sequencing.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);
}

/// <summary>
/// Process-wide cache of <see cref="DocumentRuntime"/> instances. Loading from persistence is
/// serialized per document so two connections joining at once do not race to rebuild state.
/// </summary>
public interface IDocumentRuntimeCache
{
    Task<DocumentRuntime> GetOrLoadAsync(Guid documentId, Func<CancellationToken, Task<DocumentRuntime>> loader, CancellationToken ct);
    DocumentRuntime? TryGet(Guid documentId);
    void Evict(Guid documentId);
}
