namespace Archaeologist.Core;

/// <summary>A directed graph over string-named nodes. Every enumeration order is sorted.</summary>
public sealed class DiGraph
{
    private readonly SortedDictionary<string, List<Edge>> _out = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, List<Edge>> _in = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _nodes = new(StringComparer.Ordinal);

    public DiGraph(IEnumerable<string> nodes, IEnumerable<Edge> edges)
    {
        foreach (var n in nodes) Add(n);
        foreach (var e in edges.OrderBy(e => e.From, StringComparer.Ordinal)
                     .ThenBy(e => e.To, StringComparer.Ordinal))
        {
            Add(e.From);
            Add(e.To);
            if (e.From == e.To) continue;
            _out[e.From].Add(e);
            _in[e.To].Add(e);
        }
    }

    private void Add(string n)
    {
        if (!_nodes.Add(n)) return;
        _out[n] = [];
        _in[n] = [];
    }

    public IReadOnlyCollection<string> Nodes => _nodes;
    public IReadOnlyList<Edge> Out(string n) => _out.TryGetValue(n, out var l) ? l : [];
    public IReadOnlyList<Edge> In(string n) => _in.TryGetValue(n, out var l) ? l : [];
    public IEnumerable<Edge> Edges => _nodes.SelectMany(n => _out[n]);
    public int EdgeCount => _nodes.Sum(n => _out[n].Count);

    public DiGraph InducedOn(IEnumerable<string> subset)
    {
        var s = subset.ToHashSet(StringComparer.Ordinal);
        return new DiGraph(s, Edges.Where(e => s.Contains(e.From) && s.Contains(e.To)));
    }

    public DiGraph Without(IEnumerable<Edge> cut)
    {
        var c = cut.Select(e => (e.From, e.To)).ToHashSet();
        return new DiGraph(_nodes, Edges.Where(e => !c.Contains((e.From, e.To))));
    }
}

public static class Graphs
{
    /// <summary>
    /// Tarjan. Components come back in a deterministic order (sorted by their smallest
    /// member) because a migration plan that reorders itself between runs is not a plan.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> StronglyConnectedComponents(DiGraph g)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var low = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var result = new List<List<string>>();
        var next = 0;

        foreach (var root in g.Nodes)
        {
            if (index.ContainsKey(root)) continue;

            // Explicit stack: a 400-assembly estate with a long chain will blow the CLR
            // stack on the textbook recursive formulation, and it does so in production
            // rather than in the test.
            var work = new Stack<(string node, int child)>();
            work.Push((root, 0));
            index[root] = low[root] = next++;
            stack.Push(root);
            onStack.Add(root);

            while (work.Count > 0)
            {
                var (v, ci) = work.Pop();
                var outs = g.Out(v);
                if (ci < outs.Count)
                {
                    work.Push((v, ci + 1));
                    var w = outs[ci].To;
                    if (!index.ContainsKey(w))
                    {
                        index[w] = low[w] = next++;
                        stack.Push(w);
                        onStack.Add(w);
                        work.Push((w, 0));
                    }
                    else if (onStack.Contains(w))
                    {
                        low[v] = Math.Min(low[v], index[w]);
                    }
                    continue;
                }

                if (low[v] == index[v])
                {
                    var comp = new List<string>();
                    string popped;
                    do
                    {
                        popped = stack.Pop();
                        onStack.Remove(popped);
                        comp.Add(popped);
                    } while (popped != v);
                    comp.Sort(StringComparer.Ordinal);
                    result.Add(comp);
                }

                if (work.Count > 0)
                {
                    var parent = work.Peek().node;
                    low[parent] = Math.Min(low[parent], low[v]);
                }
            }
        }

        return result
            .OrderBy(c => c[0], StringComparer.Ordinal)
            .Select(IReadOnlyList<string> (c) => c)
            .ToList();
    }

    /// <summary>Topological order. Throws if the graph still has a cycle -- silence would be worse.</summary>
    public static IReadOnlyList<string> TopologicalOrder(DiGraph g)
    {
        var indeg = g.Nodes.ToDictionary(n => n, n => g.In(n).Count, StringComparer.Ordinal);
        var ready = new SortedSet<string>(indeg.Where(kv => kv.Value == 0).Select(kv => kv.Key), StringComparer.Ordinal);
        var order = new List<string>();
        while (ready.Count > 0)
        {
            var n = ready.Min!;
            ready.Remove(n);
            order.Add(n);
            foreach (var e in g.Out(n))
                if (--indeg[e.To] == 0) ready.Add(e.To);
        }
        if (order.Count != g.Nodes.Count)
            throw new InvalidOperationException(
                $"graph still cyclic: {g.Nodes.Count - order.Count} nodes unplaced");
        return order;
    }

    /// <summary>
    /// Layer assignment: a node's wave is one past the deepest thing it depends on.
    /// Migration runs bottom-up, so wave 1 is what can move on Monday.
    /// </summary>
    public static IReadOnlyDictionary<string, int> Waves(DiGraph g)
    {
        var wave = new SortedDictionary<string, int>(StringComparer.Ordinal);
        // An edge A->B means "A depends on B", so B must be assigned before A.
        foreach (var n in TopologicalOrder(g).Reverse())
            wave[n] = g.Out(n).Count == 0 ? 1 : g.Out(n).Max(e => wave[e.To]) + 1;
        return wave;
    }
}
