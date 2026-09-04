using System.Text;

namespace Collab.Domain.Structured;

/// <summary>
/// A last-writer-wins logical timestamp: (Lamport, ReplicaId). Identical in shape to the text
/// CRDT's ElementId but kept separate to make the two conflict strategies read distinctly.
/// </summary>
public readonly record struct LwwStamp(long Lamport, string ReplicaId) : IComparable<LwwStamp>
{
    public int CompareTo(LwwStamp other)
    {
        var byLamport = Lamport.CompareTo(other.Lamport);
        return byLamport != 0 ? byLamport : string.CompareOrdinal(ReplicaId, other.ReplicaId);
    }

    public override string ToString() => $"{Lamport}@{ReplicaId}";

    public static LwwStamp Parse(string s)
    {
        var at = s.IndexOf('@');
        if (at < 0) throw new FormatException($"Invalid LwwStamp '{s}'.");
        return new LwwStamp(long.Parse(s.AsSpan(0, at)), s[(at + 1)..]);
    }
}

/// <summary>
/// A version vector: per-replica highest Lamport observed. Lets us describe the structured
/// document's version and detect whether two updates were concurrent (neither dominates).
/// </summary>
public sealed class VersionVector
{
    private readonly Dictionary<string, long> _entries;

    public VersionVector() => _entries = new(StringComparer.Ordinal);
    private VersionVector(Dictionary<string, long> entries) => _entries = entries;

    public IReadOnlyDictionary<string, long> Entries => _entries;

    public long Get(string replicaId) => _entries.TryGetValue(replicaId, out var v) ? v : 0;

    /// <summary>The highest Lamport value across all replicas (0 if empty).</summary>
    public long Max()
    {
        long max = 0;
        foreach (var v in _entries.Values)
            if (v > max) max = v;
        return max;
    }

    public void Observe(string replicaId, long lamport)
    {
        if (!_entries.TryGetValue(replicaId, out var cur) || lamport > cur)
            _entries[replicaId] = lamport;
    }

    /// <summary>True when neither vector dominates the other — i.e. the histories are concurrent.</summary>
    public static bool AreConcurrent(VersionVector a, VersionVector b)
    {
        bool aGreater = false, bGreater = false;
        foreach (var key in a._entries.Keys.Union(b._entries.Keys))
        {
            var av = a.Get(key);
            var bv = b.Get(key);
            if (av > bv) aGreater = true;
            if (bv > av) bGreater = true;
        }
        return aGreater && bGreater;
    }

    public VersionVector Clone() => new(new Dictionary<string, long>(_entries, StringComparer.Ordinal));

    public override string ToString()
    {
        var sb = new StringBuilder("{");
        var first = true;
        foreach (var kvp in _entries.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (!first) sb.Append(", ");
            sb.Append(kvp.Key).Append(':').Append(kvp.Value);
            first = false;
        }
        return sb.Append('}').ToString();
    }
}
