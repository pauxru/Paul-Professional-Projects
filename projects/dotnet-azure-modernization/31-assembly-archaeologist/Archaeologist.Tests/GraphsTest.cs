using Archaeologist.Core;

namespace Archaeologist.Tests;

/// <summary>
/// The graph layer is the foundation every other conclusion rests on. If Tarjan is wrong,
/// the migration units are wrong, the waves are wrong and the report is confidently wrong.
/// These tests use graphs whose answers can be worked out on paper.
/// </summary>
public class GraphsTest
{
    private static DiGraph G(params string[] edges) =>
        new(edges.SelectMany(e => e.Split("->")),
            edges.Select(e => { var p = e.Split("->"); return new Edge(p[0], p[1], 1); }));

    private static IReadOnlyList<IReadOnlyList<string>> Scc(DiGraph g) =>
        Graphs.StronglyConnectedComponents(g);

    // ---------- DiGraph ----------

    [Fact]
    public void SelfLoopsAreDroppedBecauseATypeDependingOnItselfIsNotACycleToBreak()
    {
        var g = G("a->a", "a->b");
        Assert.Equal(1, g.EdgeCount);
        Assert.Equal("b", g.Out("a").Single().To);
    }

    [Fact]
    public void ParallelEdgesAreKeptSeparatelySoWeightIsNotSilentlyCollapsed()
    {
        var g = new DiGraph(["a", "b"], [new Edge("a", "b", 2), new Edge("a", "b", 3)]);
        Assert.Equal(2, g.EdgeCount);
        Assert.Equal(5, g.Out("a").Sum(e => e.Weight));
    }

    [Fact]
    public void NodesMentionedOnlyByAnEdgeStillExist()
    {
        var g = new DiGraph([], [new Edge("a", "b", 1)]);
        Assert.Equal(new[] { "a", "b" }, g.Nodes);
    }

    [Fact]
    public void EnumerationOrderIsOrdinalRegardlessOfInsertionOrder()
    {
        var forward = new DiGraph(["a", "B", "c"], []);
        var reverse = new DiGraph(["c", "B", "a"], []);
        Assert.Equal(forward.Nodes, reverse.Nodes);
        Assert.Equal(new[] { "B", "a", "c" }, forward.Nodes); // 'B' < 'a' in ordinal
    }

    [Fact]
    public void InducedOnKeepsOnlyEdgesWithBothEndsInsideTheSubset()
    {
        var g = G("a->b", "b->c", "c->a", "c->d");
        var sub = g.InducedOn(["a", "b", "c"]);
        Assert.Equal(3, sub.EdgeCount);
        Assert.DoesNotContain(sub.Nodes, n => n == "d");
    }

    [Fact]
    public void WithoutRemovesEveryParallelEdgeBetweenTheCutPairNotJustTheOneHandedIn()
    {
        var g = new DiGraph(["a", "b"], [new Edge("a", "b", 1), new Edge("a", "b", 9)]);
        var cut = g.Without([new Edge("a", "b", 1)]);
        Assert.Equal(0, cut.EdgeCount);
    }

    // ---------- Tarjan ----------

    [Fact]
    public void AnAcyclicGraphHasOnlySingletonComponents()
    {
        var g = G("a->b", "b->c", "a->c");
        Assert.All(Scc(g), c => Assert.Single(c));
        Assert.Equal(3, Scc(g).Count);
    }

    [Fact]
    public void ASimpleCycleIsOneComponent()
    {
        var g = G("a->b", "b->c", "c->a");
        var big = Scc(g).Single(c => c.Count > 1);
        Assert.Equal(new[] { "a", "b", "c" }, big);
    }

    [Fact]
    public void TwoCyclesSharingANodeAreOneComponentNotTwo()
    {
        // This is the shape that broke my hand-drawn ground truth: small cycles interlock.
        var g = G("a->b", "b->a", "b->c", "c->b");
        var comps = Scc(g);
        Assert.Single(comps, c => c.Count > 1);
        Assert.Equal(3, comps.Single(c => c.Count > 1).Count);
    }

    [Fact]
    public void TwoDisjointCyclesStayTwoComponents()
    {
        var g = G("a->b", "b->a", "c->d", "d->c");
        Assert.Equal(2, Scc(g).Count(c => c.Count > 1));
    }

    [Fact]
    public void AOneWayEdgeBetweenTwoCyclesDoesNotMergeThem()
    {
        var g = G("a->b", "b->a", "b->c", "c->d", "d->c");
        Assert.Equal(2, Scc(g).Count(c => c.Count > 1));
    }

    [Fact]
    public void ComponentsAreReturnedInADeterministicOrderAndAreThemselvesSorted()
    {
        var g = G("z->y", "y->z", "b->a", "a->b");
        var comps = Scc(g);
        Assert.Equal(comps, Scc(g)); // same twice
        Assert.All(comps, c => Assert.Equal(c.OrderBy(x => x, StringComparer.Ordinal), c));
    }

    [Fact]
    public void EveryNodeAppearsInExactlyOneComponent()
    {
        var g = G("a->b", "b->c", "c->a", "c->d", "d->e", "e->d");
        var all = Scc(g).SelectMany(c => c).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Equal(g.Nodes.OrderBy(n => n, StringComparer.Ordinal), all.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void TarjanIsIterativeSoADeepChainDoesNotOverflowTheStack()
    {
        // A recursive Tarjan dies here. A real estate is deeper than a toy one, and this
        // failure mode only shows up in production, which is the worst place to find it.
        const int n = 200_000;
        var nodes = Enumerable.Range(0, n).Select(i => $"n{i:d6}").ToList();
        var edges = Enumerable.Range(0, n - 1).Select(i => new Edge(nodes[i], nodes[i + 1], 1));
        var g = new DiGraph(nodes, edges);
        Assert.Equal(n, Graphs.StronglyConnectedComponents(g).Count);
    }

    [Fact]
    public void TarjanHandlesADeepCycleWithoutOverflowing()
    {
        const int n = 100_000;
        var nodes = Enumerable.Range(0, n).Select(i => $"n{i:d6}").ToList();
        var edges = Enumerable.Range(0, n).Select(i => new Edge(nodes[i], nodes[(i + 1) % n], 1)).ToList();
        var comps = Graphs.StronglyConnectedComponents(new DiGraph(nodes, edges));
        Assert.Single(comps);
        Assert.Equal(n, comps[0].Count);
    }

    [Fact]
    public void TarjanAgreesWithABruteForceReachabilityDefinitionOnRandomGraphs()
    {
        // Definition: u and v are in the same SCC iff u reaches v and v reaches u.
        for (var seed = 0; seed < 40; seed++)
        {
            var g = FeedbackArcSet.RandomDense(7, 0.30, seed);
            var reach = Closure(g);
            var byTarjan = new Dictionary<string, int>(StringComparer.Ordinal);
            var comps = Scc(g);
            for (var i = 0; i < comps.Count; i++)
                foreach (var n in comps[i]) byTarjan[n] = i;

            foreach (var u in g.Nodes)
                foreach (var v in g.Nodes)
                {
                    var together = reach[u].Contains(v) && reach[v].Contains(u);
                    Assert.Equal(together, byTarjan[u] == byTarjan[v]);
                }
        }
    }

    private static Dictionary<string, HashSet<string>> Closure(DiGraph g)
    {
        var r = g.Nodes.ToDictionary(n => n, n => new HashSet<string>(StringComparer.Ordinal) { n }, StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            foreach (var n in g.Nodes)
                foreach (var e in g.Out(n))
                    foreach (var t in r[e.To].ToList())
                        if (r[n].Add(t)) changed = true;
        } while (changed);
        return r;
    }

    // ---------- Topological order ----------

    [Fact]
    public void TopologicalOrderPutsEveryDependencyAfterItsDependent()
    {
        var g = G("a->b", "b->c", "a->c");
        var order = Graphs.TopologicalOrder(g);
        var pos = order.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);
        foreach (var e in g.Edges) Assert.True(pos[e.From] < pos[e.To]);
    }

    [Fact]
    public void TopologicalOrderRefusesACyclicGraphRatherThanReturningSomethingPlausible()
    {
        var g = G("a->b", "b->a");
        var ex = Assert.Throws<InvalidOperationException>(() => Graphs.TopologicalOrder(g));
        Assert.Contains("cycl", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TopologicalOrderContainsEveryNodeExactlyOnceIncludingIsolatedOnes()
    {
        var g = new DiGraph(["lonely"], [new Edge("a", "b", 1)]);
        var order = Graphs.TopologicalOrder(g);
        Assert.Equal(3, order.Count);
        Assert.Equal(order.Count, order.Distinct().Count());
    }

    [Fact]
    public void TopologicalOrderIsStableAcrossRuns()
    {
        var g = FeedbackArcSet.RandomDense(9, 0.2, 11);
        var acyclic = g.Without(FeedbackArcSet.Greedy(g).Cut);
        Assert.Equal(Graphs.TopologicalOrder(acyclic), Graphs.TopologicalOrder(acyclic));
    }

    // ---------- Waves ----------

    [Fact]
    public void ALeafDependencyIsInWaveOneAndItsDependentIsLater()
    {
        // a->b means "a depends on b", so b must migrate first.
        var w = Graphs.Waves(G("a->b", "b->c"));
        Assert.Equal(1, w["c"]);
        Assert.Equal(2, w["b"]);
        Assert.Equal(3, w["a"]);
    }

    [Fact]
    public void IndependentNodesShareAWaveWhichIsThePointOfComputingWaves()
    {
        var w = Graphs.Waves(G("a->c", "b->c"));
        Assert.Equal(1, w["c"]);
        Assert.Equal(w["a"], w["b"]);
    }

    [Fact]
    public void AWaveIsOneMoreThanTheDeepestThingItDependsOnNotOneMoreThanTheShallowest()
    {
        var w = Graphs.Waves(G("top->shallow", "top->deep", "deep->mid", "mid->bottom"));
        Assert.Equal(1, w["shallow"]);
        Assert.Equal(3, w["deep"]);
        Assert.Equal(4, w["top"]); // not 2, which is what "one past shallow" would give
    }

    [Fact]
    public void EveryEdgeRunsFromAHigherWaveToALowerOne()
    {
        var g = FeedbackArcSet.RandomDense(12, 0.18, 7);
        var acyclic = g.Without(FeedbackArcSet.Greedy(g).Cut);
        var w = Graphs.Waves(acyclic);
        Assert.All(acyclic.Edges, e => Assert.True(w[e.From] > w[e.To], $"{e} violates wave order"));
    }

    [Fact]
    public void WavesRefuseACyclicGraphBecauseThereIsNoOrderToReport()
    {
        Assert.Throws<InvalidOperationException>(() => Graphs.Waves(G("a->b", "b->a")));
    }
}
