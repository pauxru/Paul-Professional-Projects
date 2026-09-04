namespace Archaeologist.Core;

public sealed record FasResult(string Method, IReadOnlyList<Edge> Cut, IReadOnlyList<string> Order)
{
    public int Edges => Cut.Count;
    public int Weight => Cut.Sum(e => e.Weight);
}

/// <summary>
/// Breaking cycles is the only interesting decision in a migration plan, and it is the one
/// tools usually hide. "The graph has a cycle" is not advice. "Cut these two edges, at this
/// cost, and here is the proof that nothing cheaper exists" is.
///
/// Minimum feedback arc set is NP-hard, so this ships both a heuristic and an exact
/// solver, and reports the gap rather than asserting there is none.
/// </summary>
public static class FeedbackArcSet
{
    /// <summary>
    /// Eades, Lin and Smyth (1993). Repeatedly strip sinks and sources, then take the
    /// vertex of maximum out-degree minus in-degree. Linear time; no optimality claim.
    /// </summary>
    public static FasResult Greedy(DiGraph g)
    {
        var remaining = new SortedSet<string>(g.Nodes, StringComparer.Ordinal);
        var left = new List<string>();
        var right = new List<string>();

        int OutW(string n) => g.Out(n).Where(e => remaining.Contains(e.To)).Sum(e => e.Weight);
        int InW(string n) => g.In(n).Where(e => remaining.Contains(e.From)).Sum(e => e.Weight);

        while (remaining.Count > 0)
        {
            bool moved;
            do
            {
                moved = false;
                foreach (var n in remaining.ToList())
                    if (OutW(n) == 0)
                    {
                        right.Add(n);
                        remaining.Remove(n);
                        moved = true;
                    }
                foreach (var n in remaining.ToList())
                    if (InW(n) == 0)
                    {
                        left.Add(n);
                        remaining.Remove(n);
                        moved = true;
                    }
            } while (moved && remaining.Count > 0);

            if (remaining.Count == 0) break;

            var pick = remaining
                .OrderByDescending(n => OutW(n) - InW(n))
                .ThenBy(n => n, StringComparer.Ordinal)
                .First();
            left.Add(pick);
            remaining.Remove(pick);
        }

        right.Reverse();
        var order = left.Concat(right).ToList();
        return new FasResult("greedy-els", Backward(g, order), order);
    }

    /// <summary>
    /// Exact, by dynamic programming over subsets. f[S] is the cheapest way to place the
    /// vertices of S as a prefix of the ordering; appending v charges every edge into v
    /// whose source is not yet placed, because those are exactly the edges that will end up
    /// pointing backwards. O(2^n * n) time, O(2^n) memory.
    ///
    /// Refuses rather than degrades above <paramref name="maxNodes"/>: an exact solver that
    /// silently becomes a heuristic is worse than no exact solver.
    /// </summary>
    public static FasResult Exact(DiGraph g, int maxNodes = 20)
    {
        var nodes = g.Nodes.OrderBy(n => n, StringComparer.Ordinal).ToList();
        var n = nodes.Count;
        if (n > maxNodes)
            throw new InvalidOperationException(
                $"exact feedback arc set refused for {n} nodes (limit {maxNodes}); " +
                "the answer would take longer than the migration");
        if (n == 0) return new FasResult("exact-dp", [], []);

        var idx = nodes.Select((x, i) => (x, i)).ToDictionary(t => t.x, t => t.i, StringComparer.Ordinal);
        var w = new int[n, n];
        foreach (var e in g.Edges) w[idx[e.From], idx[e.To]] += e.Weight;

        var size = 1 << n;
        var f = new int[size];
        var choice = new int[size];
        Array.Fill(f, int.MaxValue);
        f[0] = 0;
        Array.Fill(choice, -1);

        for (var s = 0; s < size; s++)
        {
            if (f[s] == int.MaxValue) continue;
            for (var v = 0; v < n; v++)
            {
                if ((s & (1 << v)) != 0) continue;
                var placed = s | (1 << v);
                var cost = 0;
                for (var u = 0; u < n; u++)
                    if ((placed & (1 << u)) == 0) cost += w[u, v];
                var total = f[s] + cost;
                if (total >= f[placed]) continue;
                f[placed] = total;
                choice[placed] = v;
            }
        }

        var order = new List<string>();
        for (var s = size - 1; s > 0;)
        {
            var v = choice[s];
            order.Add(nodes[v]);
            s &= ~(1 << v);
        }
        order.Reverse();

        var cut = Backward(g, order);
        if (cut.Sum(e => e.Weight) != f[size - 1])
            throw new InvalidOperationException(
                $"exact solver disagrees with its own ordering: dp={f[size - 1]} order={cut.Sum(e => e.Weight)}");
        return new FasResult("exact-dp", cut, order);
    }

    /// <summary>Edges that point backwards in the given ordering. These are the cut.</summary>
    public static IReadOnlyList<Edge> Backward(DiGraph g, IReadOnlyList<string> order)
    {
        var pos = order.Select((x, i) => (x, i)).ToDictionary(t => t.x, t => t.i, StringComparer.Ordinal);
        return g.Edges
            .Where(e => pos.TryGetValue(e.From, out var a) && pos.TryGetValue(e.To, out var b) && a > b)
            .OrderBy(e => e.From, StringComparer.Ordinal)
            .ThenBy(e => e.To, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Deterministic random digraphs, used to find the size at which the heuristic starts
    /// to cost real money. Seeded, so the reported gap is reproducible.
    /// </summary>
    public static DiGraph RandomDense(int n, double density, int seed, int maxWeight = 4)
    {
        var rng = new Random(seed);
        var nodes = Enumerable.Range(0, n).Select(i => $"n{i:00}").ToList();
        var edges = new List<Edge>();
        foreach (var a in nodes)
        foreach (var b in nodes)
            if (a != b && rng.NextDouble() < density)
                edges.Add(new Edge(a, b, rng.Next(1, maxWeight + 1)));
        return new DiGraph(nodes, edges);
    }
}
