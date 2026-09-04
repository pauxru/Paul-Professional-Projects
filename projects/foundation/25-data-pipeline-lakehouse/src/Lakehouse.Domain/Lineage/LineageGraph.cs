using System.Text;

namespace Lakehouse.Domain.Lineage;

/// <summary>A fully-qualified reference to a column in a dataset (e.g. silver.orders.amount_usd).</summary>
public sealed record ColumnRef(string Dataset, string Column)
{
    public override string ToString() => $"{Dataset}.{Column}";

    public static ColumnRef Parse(string qualified)
    {
        var idx = qualified.LastIndexOf('.');
        if (idx <= 0) throw new ArgumentException($"'{qualified}' is not a dataset.column reference.", nameof(qualified));
        return new ColumnRef(qualified[..idx], qualified[(idx + 1)..]);
    }
}

/// <summary>
/// A column-level lineage edge: a target column is derived from one or more source columns via a named
/// transformation. Edges are declared by the pipeline transforms, not hand-drawn, so the graph is always
/// consistent with the code that produced it.
/// </summary>
public sealed record LineageEdge(ColumnRef Target, IReadOnlyList<ColumnRef> Sources, string Transform);

/// <summary>
/// A directed acyclic graph of column-level lineage. Supports transitive upstream (provenance) and
/// downstream (impact analysis) queries and renders to Mermaid for the docs.
/// </summary>
public sealed class LineageGraph
{
    private readonly List<LineageEdge> _edges = new();
    private readonly Dictionary<ColumnRef, List<ColumnRef>> _forward = new();  // source -> targets
    private readonly Dictionary<ColumnRef, List<LineageEdge>> _incoming = new(); // target -> edges

    public IReadOnlyList<LineageEdge> Edges => _edges;

    public LineageGraph Add(LineageEdge edge)
    {
        _edges.Add(edge);
        _incoming.TryAdd(edge.Target, new List<LineageEdge>());
        _incoming[edge.Target].Add(edge);
        foreach (var src in edge.Sources)
        {
            _forward.TryAdd(src, new List<ColumnRef>());
            _forward[src].Add(edge.Target);
        }
        return this;
    }

    public LineageGraph Add(ColumnRef target, string transform, params ColumnRef[] sources)
        => Add(new LineageEdge(target, sources, transform));

    public IReadOnlyCollection<ColumnRef> AllColumns()
    {
        var set = new HashSet<ColumnRef>();
        foreach (var e in _edges)
        {
            set.Add(e.Target);
            foreach (var s in e.Sources) set.Add(s);
        }
        return set;
    }

    /// <summary>All columns that feed (transitively) into the target — its provenance.</summary>
    public IReadOnlyList<ColumnRef> Upstream(ColumnRef target)
    {
        var seen = new HashSet<ColumnRef>();
        var stack = new Stack<ColumnRef>();
        stack.Push(target);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!_incoming.TryGetValue(node, out var edges)) continue;
            foreach (var e in edges)
                foreach (var s in e.Sources)
                    if (seen.Add(s))
                        stack.Push(s);
        }
        return seen.ToList();
    }

    /// <summary>Impact analysis: every column that would be affected if the given source column changed.</summary>
    public IReadOnlyList<ColumnRef> Impact(ColumnRef source)
    {
        var seen = new HashSet<ColumnRef>();
        var stack = new Stack<ColumnRef>();
        stack.Push(source);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!_forward.TryGetValue(node, out var targets)) continue;
            foreach (var t in targets)
                if (seen.Add(t))
                    stack.Push(t);
        }
        return seen.ToList();
    }

    public string ToMermaid()
    {
        var sb = new StringBuilder();
        sb.AppendLine("flowchart LR");

        var byDataset = AllColumns().GroupBy(c => c.Dataset).OrderBy(g => g.Key);
        foreach (var group in byDataset)
        {
            sb.AppendLine($"    subgraph {Sanitize(group.Key)}[\"{group.Key}\"]");
            foreach (var col in group.OrderBy(c => c.Column))
                sb.AppendLine($"        {NodeId(col)}[\"{col.Column}\"]");
            sb.AppendLine("    end");
        }

        foreach (var edge in _edges)
            foreach (var src in edge.Sources)
                sb.AppendLine($"    {NodeId(src)} -->|{edge.Transform}| {NodeId(edge.Target)}");

        return sb.ToString();
    }

    private static string NodeId(ColumnRef c) => "n" + Math.Abs(HashCode.Combine(c.Dataset, c.Column)).ToString();
    private static string Sanitize(string s) => "g_" + new string(s.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
}
