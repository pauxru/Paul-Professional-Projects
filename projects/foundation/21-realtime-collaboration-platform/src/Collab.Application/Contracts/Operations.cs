using Collab.Domain.Crdt;
using Collab.Domain.Structured;

namespace Collab.Application.Contracts;

/// <summary>Wire form of a single primitive text CRDT operation.</summary>
public sealed record TextOpDto(string Type, string Id, string Reference, string Value)
{
    public static TextOpDto From(RgaOperation op) => new(
        op.Type == RgaOpType.Insert ? "insert" : "delete",
        op.Type == RgaOpType.Insert ? op.Id.ToString() : string.Empty,
        op.Reference.ToString(),
        op.Type == RgaOpType.Insert ? op.Value.ToString() : string.Empty);

    public RgaOperation ToDomain() => Type == "insert"
        ? RgaOperation.Insert(ElementId.Parse(Id), ElementId.Parse(Reference), Value.Length > 0 ? Value[0] : '\0')
        : RgaOperation.Delete(ElementId.Parse(Reference));
}

/// <summary>Wire form of a single structured (LWW) operation.</summary>
public sealed record StructuredOpDto(string Type, string ItemId, string? Field, string? Value, string Stamp)
{
    public static StructuredOpDto From(StructuredOperation op) => new(
        op.Type switch
        {
            StructuredOpType.AddItem => "add",
            StructuredOpType.RemoveItem => "remove",
            _ => "set"
        },
        op.ItemId, op.Field, op.Value, op.Stamp.ToString());

    public StructuredOperation ToDomain()
    {
        var stamp = ParseStamp(Stamp);
        return Type switch
        {
            "add" => StructuredOperation.AddItem(ItemId, stamp),
            "remove" => StructuredOperation.RemoveItem(ItemId, stamp),
            _ => StructuredOperation.SetField(ItemId, Field ?? throw new ArgumentException("Field required"), Value ?? string.Empty, stamp)
        };
    }

    private static LwwStamp ParseStamp(string s)
    {
        var at = s.IndexOf('@');
        if (at < 0) throw new FormatException($"Invalid stamp '{s}'.");
        return new LwwStamp(long.Parse(s.AsSpan(0, at)), s[(at + 1)..]);
    }
}

/// <summary>
/// A client's submission to the hub: an atomic change set of primitive operations for one document,
/// tagged with the client's replica id and an optional client-generated id for ack correlation.
/// </summary>
public sealed record OperationEnvelope(
    Guid DocumentId,
    string ReplicaId,
    IReadOnlyList<TextOpDto> TextOps,
    IReadOnlyList<StructuredOpDto> StructuredOps,
    string? ClientTag = null);

/// <summary>Server broadcast of an accepted change set, carrying its authoritative sequence.</summary>
public sealed record OperationBroadcast(
    Guid DocumentId,
    long Sequence,
    Guid AuthorUserId,
    string ReplicaId,
    IReadOnlyList<TextOpDto> TextOps,
    IReadOnlyList<StructuredOpDto> StructuredOps,
    string? ClientTag);

/// <summary>Acknowledgement returned to the submitting client.</summary>
public sealed record OperationAck(Guid DocumentId, long Sequence, string? ClientTag);

/// <summary>A well-defined rejection returned to the client instead of silently corrupting state.</summary>
public sealed record OperationRejected(Guid DocumentId, string Code, string Reason, string? ClientTag);

/// <summary>
/// Persisted form of one accepted change set, stored as the <c>Payload</c> of an operation-log
/// entry. Uniform across document types so replay code is type-agnostic.
/// </summary>
public sealed record OperationPayload(
    string ReplicaId,
    IReadOnlyList<TextOpDto> TextOps,
    IReadOnlyList<StructuredOpDto> StructuredOps);

/// <summary>Outcome of applying a change set, returned by the collaboration engine to the hub.</summary>
public sealed record ApplyResult(
    bool Accepted,
    OperationBroadcast? Broadcast,
    OperationRejected? Rejection)
{
    public static ApplyResult Ok(OperationBroadcast broadcast) => new(true, broadcast, null);
    public static ApplyResult Reject(Guid documentId, string code, string reason, string? clientTag) =>
        new(false, null, new OperationRejected(documentId, code, reason, clientTag));
}
