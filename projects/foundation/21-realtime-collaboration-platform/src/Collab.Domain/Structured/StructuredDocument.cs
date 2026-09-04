using System.Text;
using System.Text.Json;

namespace Collab.Domain.Structured;

public enum StructuredOpType
{
    AddItem = 0,
    RemoveItem = 1,
    SetField = 2
}

/// <summary>
/// One operation against the structured (checklist) document. Item membership is an
/// LWW-Element-Set; each field is an independent LWW register. Every operation carries the
/// author's <see cref="LwwStamp"/> so concurrent writes resolve deterministically by max stamp.
/// </summary>
public sealed record StructuredOperation(
    StructuredOpType Type,
    string ItemId,
    string? Field,
    string? Value,
    LwwStamp Stamp)
{
    public static StructuredOperation AddItem(string itemId, LwwStamp stamp) =>
        new(StructuredOpType.AddItem, itemId, null, null, stamp);

    public static StructuredOperation RemoveItem(string itemId, LwwStamp stamp) =>
        new(StructuredOpType.RemoveItem, itemId, null, null, stamp);

    public static StructuredOperation SetField(string itemId, string field, string value, LwwStamp stamp) =>
        new(StructuredOpType.SetField, itemId, field, value, stamp);
}

/// <summary>
/// A structured JSON document (a checklist / task list) whose conflicts are resolved by
/// field-level Last-Writer-Wins keyed on a version-vector-backed logical clock.
///
/// This is deliberately a DIFFERENT strategy from the text RGA: for a list of records where a cell
/// is either "done" or "not done", intention preservation of a character stream is meaningless, and
/// LWW-per-field gives the behaviour a user expects (the most recent toggle wins). Demonstrating
/// that different data shapes deserve different conflict strategies is the point of this contrast.
///
/// Convergence: each field is a max-register (commutative, associative, idempotent) and membership
/// is an LWW-Element-Set. The merged document is a pure function of the operation set.
/// </summary>
public sealed class StructuredDocument
{
    private sealed class Item
    {
        public LwwStamp? Added;
        public LwwStamp? Removed;
        public Dictionary<string, (string Value, LwwStamp Stamp)> Fields { get; } = new(StringComparer.Ordinal);
        public bool Present => Added is { } a && (Removed is not { } r || a.CompareTo(r) >= 0);
    }

    private readonly Dictionary<string, Item> _items = new(StringComparer.Ordinal);

    public VersionVector Version { get; } = new();

    /// <summary>Highest Lamport across all replicas that have written to this document.</summary>
    public long MaxLamport => Version.Max();

    public IReadOnlyCollection<string> PresentItemIds =>
        _items.Where(kv => kv.Value.Present).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray();

    public bool HasItem(string itemId) => _items.TryGetValue(itemId, out var i) && i.Present;

    public string? GetField(string itemId, string field) =>
        _items.TryGetValue(itemId, out var i) && i.Fields.TryGetValue(field, out var f) ? f.Value : null;

    /// <summary>Apply one operation. Idempotent and commutative; safe to replay in any order.</summary>
    public ApplyOutcome Apply(StructuredOperation op)
    {
        Version.Observe(op.Stamp.ReplicaId, op.Stamp.Lamport);
        if (!_items.TryGetValue(op.ItemId, out var item))
        {
            item = new Item();
            _items[op.ItemId] = item;
        }

        switch (op.Type)
        {
            case StructuredOpType.AddItem:
                if (item.Added is { } a && a.CompareTo(op.Stamp) >= 0) return ApplyOutcome.Ignored;
                item.Added = op.Stamp;
                return ApplyOutcome.Applied;

            case StructuredOpType.RemoveItem:
                if (item.Removed is { } r && r.CompareTo(op.Stamp) >= 0) return ApplyOutcome.Ignored;
                item.Removed = op.Stamp;
                return ApplyOutcome.Applied;

            case StructuredOpType.SetField:
                var field = op.Field ?? throw new ArgumentException("SetField requires a field name.");
                if (item.Fields.TryGetValue(field, out var existing) && existing.Stamp.CompareTo(op.Stamp) >= 0)
                    return ApplyOutcome.Ignored;
                item.Fields[field] = (op.Value ?? string.Empty, op.Stamp);
                return ApplyOutcome.Applied;

            default:
                return ApplyOutcome.Ignored;
        }
    }

    /// <summary>Deterministic canonical JSON of the present items (used for convergence assertions).</summary>
    public string ToCanonicalJson()
    {
        var sb = new StringBuilder("[");
        var first = true;
        foreach (var id in PresentItemIds)
        {
            if (!first) sb.Append(',');
            var item = _items[id];
            sb.Append("{\"id\":").Append(JsonSerializer.Serialize(id));
            foreach (var field in item.Fields.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                sb.Append(',')
                  .Append(JsonSerializer.Serialize(field))
                  .Append(':')
                  .Append(JsonSerializer.Serialize(item.Fields[field].Value));
            }
            sb.Append('}');
            first = false;
        }
        return sb.Append(']').ToString();
    }

    /// <summary>Serialize the full state (including LWW stamps) for a checkpoint.</summary>
    /// <summary>Serialize the full state (including LWW stamps) for a checkpoint.</summary>
    public StructuredState ExportState()
    {
        var items = new List<StructuredItemState>(_items.Count);
        foreach (var kvp in _items.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var item = kvp.Value;
            var fields = item.Fields
                .OrderBy(f => f.Key, StringComparer.Ordinal)
                .Select(f => new StructuredFieldState(f.Key, f.Value.Value, f.Value.Stamp.ToString()))
                .ToList();
            items.Add(new StructuredItemState(
                kvp.Key,
                item.Added?.ToString(),
                item.Removed?.ToString(),
                fields));
        }
        return new StructuredState(items);
    }

    /// <summary>Rebuild a structured document from a checkpoint produced by <see cref="ExportState"/>.</summary>
    public static StructuredDocument FromState(StructuredState state)
    {
        var doc = new StructuredDocument();
        foreach (var item in state.Items)
        {
            if (item.Added is { } added)
                doc.Apply(StructuredOperation.AddItem(item.Id, LwwStamp.Parse(added)));
            if (item.Removed is { } removed)
                doc.Apply(StructuredOperation.RemoveItem(item.Id, LwwStamp.Parse(removed)));
            foreach (var f in item.Fields)
                doc.Apply(StructuredOperation.SetField(item.Id, f.Field, f.Value, LwwStamp.Parse(f.Stamp)));
        }
        return doc;
    }
}

public sealed record StructuredFieldState(string Field, string Value, string Stamp);
public sealed record StructuredItemState(string Id, string? Added, string? Removed, IReadOnlyList<StructuredFieldState> Fields);
public sealed record StructuredState(IReadOnlyList<StructuredItemState> Items);

public enum ApplyOutcome
{
    Applied = 0,
    /// <summary>The operation lost the LWW race (an equal or newer stamp already won).</summary>
    Ignored = 1
}
