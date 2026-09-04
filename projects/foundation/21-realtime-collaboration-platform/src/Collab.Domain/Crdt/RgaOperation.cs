namespace Collab.Domain.Crdt;

public enum RgaOpType
{
    Insert = 0,
    Delete = 1
}

/// <summary>
/// A single primitive operation on the text CRDT. A user keystroke of several characters is
/// expanded into several of these (one per character) so that the merge logic only ever reasons
/// about one element at a time — which is what keeps the convergence proof tractable.
///
/// Insert: creates element <see cref="Id"/> holding <see cref="Value"/>, positioned immediately
/// after <see cref="Reference"/> (the parent; <see cref="ElementId.Root"/> means "at the start").
/// Delete: tombstones the element identified by <see cref="Reference"/>. Deletes never remove the
/// node, so any concurrent insert that referenced it still has a valid parent.
/// </summary>
public sealed record RgaOperation(
    RgaOpType Type,
    ElementId Id,
    ElementId Reference,
    char Value)
{
    public static RgaOperation Insert(ElementId id, ElementId parent, char value) =>
        new(RgaOpType.Insert, id, parent, value);

    public static RgaOperation Delete(ElementId target) =>
        new(RgaOpType.Delete, default, target, '\0');

    /// <summary>The replica that authored this operation (origin of its Lamport timestamp).</summary>
    public string ReplicaId => Type == RgaOpType.Insert ? Id.ReplicaId : Reference.ReplicaId;

    /// <summary>The Lamport timestamp associated with the operation.</summary>
    public long Lamport => Type == RgaOpType.Insert ? Id.Lamport : Reference.Lamport;
}
