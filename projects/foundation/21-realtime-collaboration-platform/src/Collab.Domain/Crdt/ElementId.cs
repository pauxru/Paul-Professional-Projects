using System.Globalization;

namespace Collab.Domain.Crdt;

/// <summary>
/// Globally-unique identity of a single character element in the RGA/causal-tree text CRDT.
/// Identity is the pair (Lamport timestamp, ReplicaId). The Lamport component provides causal
/// ordering; the ReplicaId breaks ties between concurrent operations so that every replica
/// derives the same total order and therefore converges.
/// </summary>
public readonly record struct ElementId(long Lamport, string ReplicaId) : IComparable<ElementId>
{
    /// <summary>
    /// The invisible root/sentinel element. Every top-level insertion is a child of the root.
    /// It is never rendered and always exists, so an "insert at the very beginning" always has a
    /// valid, already-present parent.
    /// </summary>
    public static readonly ElementId Root = new(0, string.Empty);

    public bool IsRoot => Lamport == 0 && ReplicaId.Length == 0;

    /// <summary>
    /// Deterministic total order. Higher Lamport sorts greater; ties broken by ordinal ReplicaId.
    /// This ordering is the sole reason concurrent inserts converge identically on all replicas.
    /// </summary>
    public int CompareTo(ElementId other)
    {
        var byLamport = Lamport.CompareTo(other.Lamport);
        return byLamport != 0
            ? byLamport
            : string.CompareOrdinal(ReplicaId, other.ReplicaId);
    }

    /// <summary>Compact canonical form used on the wire and in persistence, e.g. "12@r1".</summary>
    public override string ToString() =>
        Lamport.ToString(CultureInfo.InvariantCulture) + "@" + ReplicaId;

    public static ElementId Parse(string s)
    {
        if (string.IsNullOrEmpty(s)) return Root;
        var at = s.IndexOf('@');
        if (at < 0) throw new FormatException($"Invalid ElementId '{s}'.");
        var lamport = long.Parse(s.AsSpan(0, at), CultureInfo.InvariantCulture);
        var replica = s[(at + 1)..];
        return new ElementId(lamport, replica);
    }
}
