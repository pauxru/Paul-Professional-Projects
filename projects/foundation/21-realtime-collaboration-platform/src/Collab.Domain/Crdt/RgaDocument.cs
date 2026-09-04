namespace Collab.Domain.Crdt;

/// <summary>Outcome of applying a single operation to the CRDT.</summary>
public enum ApplyStatus
{
    /// <summary>The operation was applied and changed state.</summary>
    Applied = 0,
    /// <summary>Already-seen operation (idempotent no-op). Safe to ignore.</summary>
    Duplicate = 1,
    /// <summary>Causally not ready (its reference has not arrived yet); held in the buffer.</summary>
    Buffered = 2
}

/// <summary>Serializable snapshot of a node for checkpoints.</summary>
public sealed record RgaNodeState(string Id, string Parent, char Value, bool Deleted);

/// <summary>Serializable snapshot of the whole tree for checkpoints.</summary>
public sealed record RgaState(IReadOnlyList<RgaNodeState> Nodes);

/// <summary>
/// A Replicated Growable Array (RGA) for plain text, implemented as a timestamped causal tree.
///
/// Each character is a node with a globally-unique <see cref="ElementId"/> and a reference to the
/// node it was inserted after (its parent; the sentinel <see cref="ElementId.Root"/> for the start
/// of the document). The linear text is a pre-order traversal of the tree in which the children of
/// any node are visited in DESCENDING id order, so a newer concurrent insert appears closer to its
/// anchor. Deletes are tombstones — the node is retained so concurrent inserts keep a valid parent.
///
/// Convergence: the final tree is a pure function of the SET of operations (add-node / tombstone),
/// both of which are commutative and idempotent, and the traversal order is deterministic. Given
/// the same set of operations, every replica renders identical text regardless of arrival order.
/// Out-of-order arrivals are handled by <see cref="_pending"/> (the causal buffer).
///
/// Not thread-safe by design: the server serializes access per document.
/// </summary>
public sealed class RgaDocument
{
    private sealed class Node(ElementId id, ElementId parent, char value)
    {
        public ElementId Id { get; } = id;
        public ElementId Parent { get; } = parent;
        public char Value { get; } = value;
        public bool Deleted { get; set; }
        // Children kept sorted DESCENDING by id so traversal is deterministic across replicas.
        public List<ElementId> Children { get; } = [];
    }

    private readonly Dictionary<ElementId, Node> _nodes = [];
    private readonly Dictionary<ElementId, List<ElementId>> _childIndex = [];
    private readonly List<RgaOperation> _pending = [];

    public RgaDocument()
    {
        // The root sentinel always exists.
        _childIndex[ElementId.Root] = [];
    }

    /// <summary>Number of buffered operations waiting on a causal dependency.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>Count of visible (non-tombstoned) characters — the rendered length.</summary>
    public int Length
    {
        get
        {
            var n = 0;
            foreach (var node in _nodes.Values)
                if (!node.Deleted) n++;
            return n;
        }
    }

    /// <summary>Total elements including tombstones (memory footprint indicator).</summary>
    public int TotalElements => _nodes.Count;

    /// <summary>The highest Lamport timestamp among all elements (0 for an empty document).</summary>
    public long MaxLamport
    {
        get
        {
            long max = 0;
            foreach (var id in _nodes.Keys)
                if (id.Lamport > max) max = id.Lamport;
            return max;
        }
    }

    public bool Contains(ElementId id) => id.IsRoot || _nodes.ContainsKey(id);

    /// <summary>
    /// Apply one operation. Idempotent and commutative. If the operation references a not-yet-known
    /// element it is buffered and retried automatically as its dependency arrives.
    /// </summary>
    public ApplyStatus Apply(RgaOperation op)
    {
        var status = ApplyCore(op);
        if (status == ApplyStatus.Applied)
            DrainPending();
        return status;
    }

    private ApplyStatus ApplyCore(RgaOperation op)
    {
        switch (op.Type)
        {
            case RgaOpType.Insert:
                if (_nodes.ContainsKey(op.Id)) return ApplyStatus.Duplicate;
                if (!Contains(op.Reference)) { Buffer(op); return ApplyStatus.Buffered; }
                LinkInsert(op.Id, op.Reference, op.Value);
                return ApplyStatus.Applied;

            case RgaOpType.Delete:
                if (!_nodes.TryGetValue(op.Reference, out var target))
                {
                    Buffer(op);
                    return ApplyStatus.Buffered;
                }
                if (target.Deleted) return ApplyStatus.Duplicate;
                target.Deleted = true;
                return ApplyStatus.Applied;

            default:
                return ApplyStatus.Duplicate;
        }
    }

    private void Buffer(RgaOperation op)
    {
        // Avoid unbounded duplicates in the buffer.
        if (!_pending.Contains(op)) _pending.Add(op);
    }

    private void DrainPending()
    {
        bool progress;
        do
        {
            progress = false;
            for (var i = _pending.Count - 1; i >= 0; i--)
            {
                var op = _pending[i];
                var ready = op.Type == RgaOpType.Insert
                    ? Contains(op.Reference) && !_nodes.ContainsKey(op.Id)
                    : _nodes.ContainsKey(op.Reference);
                if (!ready)
                {
                    // Drop exact duplicates that became satisfied elsewhere.
                    if (op.Type == RgaOpType.Insert && _nodes.ContainsKey(op.Id))
                        _pending.RemoveAt(i);
                    continue;
                }

                _pending.RemoveAt(i);
                if (ApplyCore(op) == ApplyStatus.Applied)
                    progress = true;
            }
        }
        while (progress);
    }

    private void LinkInsert(ElementId id, ElementId parent, char value)
    {
        var node = new Node(id, parent, value);
        _nodes[id] = node;
        _childIndex[id] = node.Children;

        var siblings = _childIndex[parent];
        // Insert while keeping the list sorted DESCENDING by id.
        var pos = 0;
        while (pos < siblings.Count && siblings[pos].CompareTo(id) > 0)
            pos++;
        siblings.Insert(pos, id);
    }

    /// <summary>Render the current document text.</summary>
    public string Materialize()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var id in Traverse())
        {
            var node = _nodes[id];
            if (!node.Deleted) sb.Append(node.Value);
        }
        return sb.ToString();
    }

    /// <summary>The ids of visible characters, in document order.</summary>
    public IReadOnlyList<ElementId> VisibleElementIds()
    {
        var result = new List<ElementId>();
        foreach (var id in Traverse())
            if (!_nodes[id].Deleted) result.Add(id);
        return result;
    }

    /// <summary>
    /// The zero-based offset of an element among the visible characters, or -1 if it is unknown or
    /// tombstoned. Used to rebase comment anchors as individual operations are applied.
    /// </summary>
    public int VisibleIndexOf(ElementId id)
    {
        var index = 0;
        foreach (var e in Traverse())
        {
            if (_nodes[e].Deleted) continue;
            if (e.Equals(id)) return index;
            index++;
        }
        return -1;
    }

    /// <summary>
    /// Pre-order traversal of all nodes (including tombstones), children visited in descending id
    /// order. Uses an explicit stack to avoid deep recursion on long documents.
    /// </summary>
    private IEnumerable<ElementId> Traverse()
    {
        var stack = new Stack<ElementId>();
        // Root's children pushed ascending so the largest is processed first.
        PushChildrenAscending(stack, ElementId.Root);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            yield return id;
            PushChildrenAscending(stack, id);
        }
    }

    private void PushChildrenAscending(Stack<ElementId> stack, ElementId parent)
    {
        if (!_childIndex.TryGetValue(parent, out var children)) return;
        // children is descending; push in reverse (ascending) so the largest pops first.
        for (var i = children.Count - 1; i >= 0; i--)
            stack.Push(children[i]);
    }

    /// <summary>
    /// The element to use as the parent when inserting so a new character lands at <paramref name="index"/>.
    /// Index 0 (or below) inserts at the very start (root); index &gt;= length appends at the end.
    /// </summary>
    public ElementId ReferenceForInsertAt(int index)
    {
        if (index <= 0) return ElementId.Root;
        var visible = VisibleElementIds();
        if (index >= visible.Count) return visible.Count == 0 ? ElementId.Root : visible[^1];
        return visible[index - 1];
    }

    /// <summary>
    /// Expand a user "insert string at index" action into primitive per-character operations,
    /// chained so they render in order. Timestamps are drawn from <paramref name="clock"/>.
    /// </summary>
    public IReadOnlyList<RgaOperation> BuildInsert(int index, string text, LamportClock clock, string replicaId)
    {
        ArgumentNullException.ThrowIfNull(text);
        var ops = new List<RgaOperation>(text.Length);
        var parent = ReferenceForInsertAt(index);
        foreach (var ch in text)
        {
            var id = new ElementId(clock.Tick(), replicaId);
            ops.Add(RgaOperation.Insert(id, parent, ch));
            parent = id;
        }
        return ops;
    }

    /// <summary>Expand a user "delete <paramref name="length"/> chars at <paramref name="start"/>" action.</summary>
    public IReadOnlyList<RgaOperation> BuildDelete(int start, int length)
    {
        var visible = VisibleElementIds();
        var ops = new List<RgaOperation>();
        var end = Math.Min(visible.Count, start + length);
        for (var i = Math.Max(0, start); i < end; i++)
            ops.Add(RgaOperation.Delete(visible[i]));
        return ops;
    }

    /// <summary>Serialize the full tree (including tombstones) for a checkpoint.</summary>
    public RgaState ExportState()
    {
        var nodes = new List<RgaNodeState>(_nodes.Count);
        // Deterministic export ordering (document order then tombstones) aids debugging & diffing.
        foreach (var kvp in _nodes)
        {
            var n = kvp.Value;
            nodes.Add(new RgaNodeState(n.Id.ToString(), n.Parent.ToString(), n.Value, n.Deleted));
        }
        return new RgaState(nodes);
    }

    /// <summary>Rebuild a document from a checkpoint produced by <see cref="ExportState"/>.</summary>
    public static RgaDocument FromState(RgaState state)
    {
        var doc = new RgaDocument();
        // Insert in id order so every parent exists before its children (root is always present).
        var ordered = state.Nodes
            .Select(n => (Node: n, Id: ElementId.Parse(n.Id)))
            .OrderBy(x => x.Id)
            .ToList();

        foreach (var (n, id) in ordered)
        {
            var parent = ElementId.Parse(n.Parent);
            doc.LinkInsert(id, parent, n.Value);
            if (n.Deleted) doc._nodes[id].Deleted = true;
        }
        doc.DrainPending();
        return doc;
    }
}
