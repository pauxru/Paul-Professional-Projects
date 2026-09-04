using Archaeologist.Core;

namespace Archaeologist.Tests;

/// <summary>
/// Minimum feedback arc set is NP-hard, so this codebase ships a heuristic and an exact
/// solver and reports the gap between them. That claim is only worth anything if the exact
/// solver is genuinely exact, so it is checked against brute-force enumeration of every
/// permutation, and the heuristic is checked never to beat it.
/// </summary>
public class FeedbackArcSetTest
{
    private static DiGraph G(params string[] edges) =>
        new(edges.SelectMany(e => e.Split("->")),
            edges.Select(e => { var p = e.Split("->"); return new Edge(p[0], p[1], 1); }));

    private static bool IsAcyclic(DiGraph g) =>
        Graphs.StronglyConnectedComponents(g).All(c => c.Count == 1)
        && g.Edges.All(e => e.From != e.To);

    /// <summary>Every permutation, minimum backward weight. Only usable for tiny n.</summary>
    private static int BruteForce(DiGraph g)
    {
        var nodes = g.Nodes.ToArray();
        var best = int.MaxValue;
        foreach (var perm in Permutations(nodes))
        {
            var w = FeedbackArcSet.Backward(g, perm).Sum(e => e.Weight);
            if (w < best) best = w;
        }
        return best;
    }

    private static IEnumerable<string[]> Permutations(string[] items)
    {
        if (items.Length <= 1) { yield return items; yield break; }
        for (var i = 0; i < items.Length; i++)
        {
            var rest = items.Take(i).Concat(items.Skip(i + 1)).ToArray();
            foreach (var p in Permutations(rest))
                yield return new[] { items[i] }.Concat(p).ToArray();
        }
    }

    // ---------- Backward ----------

    [Fact]
    public void BackwardFindsTheEdgesThatPointAgainstTheGivenOrder()
    {
        var g = G("a->b", "b->c", "c->a");
        var back = FeedbackArcSet.Backward(g, ["a", "b", "c"]);
        Assert.Equal("c->a(1)", back.Single().ToString());
    }

    [Fact]
    public void BackwardOnATotallyReversedOrderReturnsEveryEdge()
    {
        var g = G("a->b", "b->c", "a->c");
        Assert.Equal(3, FeedbackArcSet.Backward(g, ["c", "b", "a"]).Count);
    }

    [Fact]
    public void RemovingTheBackwardEdgesOfAnyOrderAlwaysLeavesAnAcyclicGraph()
    {
        for (var seed = 0; seed < 25; seed++)
        {
            var g = FeedbackArcSet.RandomDense(8, 0.35, seed);
            var arbitrary = g.Nodes.Reverse().ToList();
            Assert.True(IsAcyclic(g.Without(FeedbackArcSet.Backward(g, arbitrary))));
        }
    }

    // ---------- Greedy ----------

    [Fact]
    public void GreedyLeavesNothingToCutOnAnAlreadyAcyclicGraph()
    {
        var g = G("a->b", "b->c", "a->c");
        Assert.Empty(FeedbackArcSet.Greedy(g).Cut);
    }

    [Fact]
    public void GreedyBreaksASimpleCycleWithOneEdge()
    {
        Assert.Single(FeedbackArcSet.Greedy(G("a->b", "b->c", "c->a")).Cut);
    }

    [Fact]
    public void GreedyPrefersCuttingTheCheapEdgeOfATwoCycle()
    {
        var g = new DiGraph(["a", "b"], [new Edge("a", "b", 9), new Edge("b", "a", 1)]);
        var cut = FeedbackArcSet.Greedy(g).Cut;
        Assert.Equal(1, cut.Sum(e => e.Weight));
    }

    [Fact]
    public void GreedyAlwaysProducesAnAcyclicResultAcrossManyRandomGraphs()
    {
        for (var seed = 0; seed < 60; seed++)
        {
            var g = FeedbackArcSet.RandomDense(10, 0.30, seed);
            Assert.True(IsAcyclic(g.Without(FeedbackArcSet.Greedy(g).Cut)), $"seed {seed}");
        }
    }

    [Fact]
    public void GreedyIsDeterministic()
    {
        var g = FeedbackArcSet.RandomDense(14, 0.25, 3);
        Assert.Equal(
            FeedbackArcSet.Greedy(g).Cut.Select(e => e.ToString()),
            FeedbackArcSet.Greedy(g).Cut.Select(e => e.ToString()));
    }

    [Fact]
    public void GreedyOrderIsAPermutationOfEveryNode()
    {
        var g = FeedbackArcSet.RandomDense(11, 0.3, 5);
        var order = FeedbackArcSet.Greedy(g).Order;
        Assert.Equal(g.Nodes.OrderBy(n => n, StringComparer.Ordinal),
                     order.OrderBy(n => n, StringComparer.Ordinal));
    }

    // ---------- Exact ----------

    [Fact]
    public void ExactMatchesBruteForcePermutationSearchOnEverySmallRandomGraph()
    {
        // The whole "greedy vs exact" measurement in the report depends on this.
        for (var seed = 0; seed < 30; seed++)
        {
            var g = FeedbackArcSet.RandomDense(6, 0.40, seed);
            Assert.Equal(BruteForce(g), FeedbackArcSet.Exact(g).Cut.Sum(e => e.Weight));
        }
    }

    [Fact]
    public void ExactMatchesBruteForceOnDenseGraphsWhereCyclesAreUnavoidable()
    {
        for (var seed = 100; seed < 115; seed++)
        {
            var g = FeedbackArcSet.RandomDense(6, 0.75, seed);
            Assert.Equal(BruteForce(g), FeedbackArcSet.Exact(g).Cut.Sum(e => e.Weight));
        }
    }

    [Fact]
    public void ExactAlwaysProducesAnAcyclicResult()
    {
        for (var seed = 0; seed < 30; seed++)
        {
            var g = FeedbackArcSet.RandomDense(9, 0.35, seed);
            Assert.True(IsAcyclic(g.Without(FeedbackArcSet.Exact(g).Cut)), $"seed {seed}");
        }
    }

    [Fact]
    public void GreedyNeverBeatsExactWhichIsTheOnlyDirectionThatCanBeAsserted()
    {
        for (var seed = 0; seed < 80; seed++)
        {
            var g = FeedbackArcSet.RandomDense(9, 0.32, seed);
            var greedy = FeedbackArcSet.Greedy(g).Cut.Sum(e => e.Weight);
            var exact = FeedbackArcSet.Exact(g).Cut.Sum(e => e.Weight);
            Assert.True(greedy >= exact, $"seed {seed}: greedy {greedy} < exact {exact}");
        }
    }

    [Fact]
    public void GreedyAndExactDisagreeSomewhereOtherwiseTheComparisonMeasuresNothing()
    {
        var divergences = 0;
        for (var seed = 0; seed < 80; seed++)
        {
            var g = FeedbackArcSet.RandomDense(9, 0.32, seed);
            if (FeedbackArcSet.Greedy(g).Cut.Sum(e => e.Weight)
                > FeedbackArcSet.Exact(g).Cut.Sum(e => e.Weight)) divergences++;
        }
        Assert.True(divergences > 0, "greedy matched exact everywhere; the report's gap claim would be vacuous");
    }

    [Fact]
    public void ExactRefusesGraphsAboveItsLimitRatherThanQuietlyBecomingAHeuristic()
    {
        // An exact solver that degrades into an approximation without saying so is worse
        // than not having one, because its output is still labelled "exact".
        var g = FeedbackArcSet.RandomDense(25, 0.2, 1);
        var ex = Assert.Throws<InvalidOperationException>(() => FeedbackArcSet.Exact(g));
        Assert.Contains("25", ex.Message);
    }

    [Fact]
    public void ExactAcceptsAGraphExactlyAtItsLimit()
    {
        var g = FeedbackArcSet.RandomDense(6, 0.3, 2);
        FeedbackArcSet.Exact(g, maxNodes: 6);
        Assert.Throws<InvalidOperationException>(() => FeedbackArcSet.Exact(g, maxNodes: 5));
    }

    [Fact]
    public void ExactHandlesTheEmptyAndSingletonCases()
    {
        Assert.Empty(FeedbackArcSet.Exact(new DiGraph([], [])).Cut);
        Assert.Empty(FeedbackArcSet.Exact(new DiGraph(["only"], [])).Cut);
    }

    [Fact]
    public void ExactReportsTheOrderThatJustifiesItsOwnCut()
    {
        // Self-consistency: re-deriving the backward set from the returned order must give
        // back the same total weight. This is what catches an off-by-one in the DP rebuild.
        for (var seed = 0; seed < 25; seed++)
        {
            var g = FeedbackArcSet.RandomDense(8, 0.35, seed);
            var r = FeedbackArcSet.Exact(g);
            Assert.Equal(r.Cut.Sum(e => e.Weight),
                         FeedbackArcSet.Backward(g, r.Order).Sum(e => e.Weight));
        }
    }

    [Fact]
    public void BothSolversLabelThemselvesSoTheReportCannotConfuseThem()
    {
        var g = G("a->b", "b->a");
        Assert.Equal("greedy-els", FeedbackArcSet.Greedy(g).Method);
        Assert.Equal("exact-dp", FeedbackArcSet.Exact(g).Method);
    }

    // ---------- RandomDense ----------

    [Fact]
    public void RandomDenseIsReproducibleFromItsSeed()
    {
        var a = FeedbackArcSet.RandomDense(12, 0.3, 42);
        var b = FeedbackArcSet.RandomDense(12, 0.3, 42);
        Assert.Equal(a.Edges.Select(e => e.ToString()), b.Edges.Select(e => e.ToString()));
    }

    [Fact]
    public void RandomDenseVariesWithItsSeed()
    {
        var a = FeedbackArcSet.RandomDense(12, 0.3, 1);
        var b = FeedbackArcSet.RandomDense(12, 0.3, 2);
        Assert.NotEqual(a.Edges.Select(e => e.ToString()), b.Edges.Select(e => e.ToString()));
    }

    [Fact]
    public void RandomDenseProducesGraphsThatActuallyContainCycles()
    {
        var withCycles = Enumerable.Range(0, 20)
            .Count(s => Graphs.StronglyConnectedComponents(FeedbackArcSet.RandomDense(8, 0.4, s))
                .Any(c => c.Count > 1));
        Assert.True(withCycles > 15, $"only {withCycles}/20 seeds were cyclic");
    }
}
