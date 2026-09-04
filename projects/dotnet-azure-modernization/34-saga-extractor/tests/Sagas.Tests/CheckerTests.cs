using Sagas.Core;

namespace Sagas.Tests;

/// <summary>
/// The checker itself: the packed attempt counter, the transition families, the
/// shortest-trace property, and one saga per property built specifically to
/// violate it.
///
/// The last group is the important one. A property that no saga can violate is a
/// property that is not being checked, and it will sit in the code looking like
/// safety while providing none.
/// </summary>
public class CheckerTests
{
    private static CheckerOptions Opts => new() { CrashBudget = 1 };

    [Fact]
    public void TheFullyFixedSagaHasNoViolations()
    {
        var res = Checker.Check(Catalogue.V7_QueryablePivot(), Opts);
        Assert.Empty(res.Violations);
    }

    [Fact]
    public void ExplorationIsDeterministic()
    {
        var a = Checker.Check(Catalogue.V4_IdempotentForwards(), Opts);
        var b = Checker.Check(Catalogue.V4_IdempotentForwards(), Opts);
        Assert.Equal(a.StatesExplored, b.StatesExplored);
        Assert.Equal(
            a.Violations.Select(v => (v.Kind, v.Depth, v.Detail)),
            b.Violations.Select(v => (v.Kind, v.Depth, v.Detail)));
    }

    [Fact]
    public void EveryReachableExecutionTerminates()
    {
        // No violation of any kind means, among other things, no stuck state --
        // so every terminal is either Done or Aborted and the saga always
        // reaches a decision.
        var res = Checker.Check(Catalogue.V7_QueryablePivot(), Opts);
        Assert.DoesNotContain(res.Violations, v => v.Kind == PropertyKind.StuckState);
        Assert.True(res.Terminals.GetValueOrDefault(Phase.Done) > 0, "no completed executions were found at all");
        Assert.True(res.Terminals.GetValueOrDefault(Phase.Aborted) > 0, "no aborted executions were found at all");
        Assert.True(res.Exhaustive, "the search hit its state cap, so it proves nothing");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TheFixedSagaIsCleanAtEveryCrashBudgetWeCanAfford(int budget)
    {
        var res = Checker.Check(Catalogue.V7_QueryablePivot(), Opts with { CrashBudget = budget });
        Assert.Empty(res.Violations);
    }

    [Fact]
    public void MoreCrashesMeansMoreStates()
    {
        var counts = Enumerable.Range(0, 4)
            .Select(b => Checker.Check(Catalogue.V7_QueryablePivot(), Opts with { CrashBudget = b })
                .StatesExplored)
            .ToList();
        Assert.Equal(counts.OrderBy(x => x), counts);
        Assert.True(counts[0] < counts[^1]);
    }

    [Fact]
    public void ABoundedSearchCanMissViolationsThatABiggerBoundFinds()
    {
        // This is the headline caveat of the whole technique, so it is pinned as
        // a test rather than left as a sentence in the report.
        var atOne = Checker.Check(Catalogue.V4_IdempotentForwards(), Opts with { CrashBudget = 1 });
        var atThree = Checker.Check(Catalogue.V4_IdempotentForwards(), Opts with { CrashBudget = 3 });
        Assert.True(
            atThree.Violations.Count > atOne.Violations.Count,
            $"expected budget 3 to expose more than budget 1, got {atThree.Violations.Count} vs {atOne.Violations.Count}");
    }

    [Fact]
    public void SkippingTheUncertainStepReintroducesDirtyAborts()
    {
        var res = Checker.Check(
            Catalogue.V7_QueryablePivot(),
            Opts with { Scope = CompensationScope.AssumeUncertainStepDidNotRun });
        Assert.Contains(res.Violations, v => v.Kind == PropertyKind.DirtyAbort);
    }

    [Fact]
    public void BoundedCompensationRetriesCreateStuckStates()
    {
        var res = Checker.Check(
            Catalogue.V7_QueryablePivot(),
            Opts with { CompensationRetriesForever = false });
        Assert.Contains(res.Violations, v => v.Kind == PropertyKind.StuckState);
    }

    [Fact]
    public void UnboundedCompensationRetriesDoNot()
    {
        var res = Checker.Check(
            Catalogue.V7_QueryablePivot(),
            Opts with { CompensationRetriesForever = true });
        Assert.DoesNotContain(res.Violations, v => v.Kind == PropertyKind.StuckState);
    }

    [Fact]
    public void EveryPropertyKindIsActuallyReachable()
    {
        // If a PropertyKind never fires anywhere in the catalogue or its
        // mutations, it is dead code masquerading as a safety check.
        var seen = new HashSet<PropertyKind>();
        foreach (var (_, _, build) in Catalogue.Progression)
        {
            foreach (var v in Checker.Check(build(), Opts).Violations)
            {
                seen.Add(v.Kind);
            }
        }

        foreach (var (_, saga) in DesignMutations.All())
        {
            foreach (var v in Checker.Check(saga, Opts).Violations)
            {
                seen.Add(v.Kind);
            }
        }

        foreach (var kind in Enum.GetValues<PropertyKind>())
        {
            Assert.Contains(kind, seen);
        }
    }

    [Fact]
    public void EveryViolationCarriesATraceFromTheInitialState()
    {
        var saga = Catalogue.V3_NeutralCompensations();
        var res = Checker.Check(saga, Opts);
        Assert.NotEmpty(res.Violations);
        foreach (var v in res.Violations)
        {
            Assert.Equal(v.Depth, v.Trace.Count);
            Assert.All(v.Trace, t => Assert.False(string.IsNullOrWhiteSpace(t.Label)));

            var rendered = v.Render(saga.Initial);
            Assert.Contains(v.Kind.ToString(), rendered, StringComparison.Ordinal);
            foreach (var t in v.Trace)
            {
                Assert.Contains(t.Label, rendered, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void TracesAreShortest()
    {
        // BFS guarantees this, but the guarantee only holds if the parent map is
        // written on first discovery rather than on every visit -- an easy bug
        // to introduce and an impossible one to notice by reading output.
        var saga = Catalogue.V3_NeutralCompensations();
        var res = Checker.Check(saga, Opts);
        foreach (var group in res.Violations.GroupBy(v => v.Kind))
        {
            var min = group.Min(v => v.Depth);
            Assert.All(group, v => Assert.True(v.Depth >= min));
        }

        Assert.All(res.Violations, v => Assert.True(v.Depth <= 8, $"trace of {v.Depth} is implausibly long"));
    }

    [Fact]
    public void OneViolationIsReportedPerDistinctProblemNotPerPath()
    {
        // Thousands of paths reach the same bad state. Reporting each one buries
        // the reader; the checker keeps the first (therefore shortest) per
        // distinct problem.
        var res = Checker.Check(Catalogue.V1_AsWritten(), Opts);
        var keys = res.Violations.Select(v => (v.Kind, v.Detail)).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void ShapeViolationsAppearAtDepthZero()
    {
        // A shape error is a fact about the design, not about an execution, so
        // it cannot have a meaningful trace.
        var res = Checker.Check(Catalogue.V1_AsWritten(), Opts);
        var shape = res.Violations.Where(v => v.Kind == PropertyKind.ShapeViolation).ToList();
        Assert.NotEmpty(shape);
        Assert.All(shape, v => Assert.Equal(0, v.Depth));
    }

    [Fact]
    public void EveryFixInTheProgressionIsLoadBearing()
    {
        foreach (var (label, saga) in DesignMutations.All())
        {
            var res = Checker.Check(saga, Opts);
            Assert.True(
                res.Violations.Count > 0,
                $"reverting '{label}' produced no violation, so that fix is not load-bearing "
                + "or the property that justified it is not being checked");
        }
    }

    [Fact]
    public void TheProgressionEndsClean()
    {
        var counts = Catalogue.Progression
            .Where(p => p.Label != "v4b")
            .Select(p => Checker.Check(p.Build(), Opts).Violations.Count)
            .ToList();
        Assert.Equal(0, counts[^1]);
        Assert.True(counts[0] > 30, $"v1 should be badly broken, found {counts[0]} violations");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void SyntheticChainsGrowMonotonicallyInStates(int n)
    {
        var smaller = Checker.Check(Synthetic.Chain(n), Opts).StatesExplored;
        var bigger = Checker.Check(Synthetic.Chain(n + 1), Opts).StatesExplored;
        Assert.True(bigger > smaller, $"chain({n + 1})={bigger} should exceed chain({n})={smaller}");
    }

    [Fact]
    public void SyntheticChainsAreThemselvesCorrect()
    {
        // The growth measurement is only meaningful if the sagas being measured
        // are sound; a chain with a defect would explore a different shape of
        // space (aborts and rollbacks) and the numbers would not mean what the
        // report says they mean.
        for (var n = 2; n <= 6; n++)
        {
            Assert.Empty(Checker.Check(Synthetic.Chain(n), Opts).Violations);
        }
    }
}
