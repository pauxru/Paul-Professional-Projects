using Archaeologist.Core;

namespace Archaeologist.Tests;

/// <summary>
/// The planner is where the project's thesis lives: the assembly is the wrong unit. These
/// tests check the plan is internally consistent -- that its waves really are a valid build
/// order, that its units really are indivisible, and that its difficulty scores really do
/// disagree with the naive per-assembly ones.
/// </summary>
public class MigrationPlannerTest : IClassFixture<EstateFixture>
{
    private readonly EstateFixture f;
    private Estate E => f.Estate;
    private readonly Plan plan;

    public MigrationPlannerTest(EstateFixture fixture)
    {
        f = fixture;
        plan = MigrationPlanner.Build(E);
    }

    // ---------- Units ----------

    [Fact]
    public void EveryAssemblyBelongsToExactlyOneUnit()
    {
        var all = plan.Units.SelectMany(u => u.Assemblies).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(E.Assemblies.Count, all.Count);
    }

    [Fact]
    public void EveryTypeBelongsToExactlyOneUnit()
    {
        var all = plan.Units.SelectMany(u => u.Types).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(E.Types.Count, all.Count);
    }

    [Fact]
    public void AUnitsTypesAreExactlyTheTypesOfItsAssemblies()
    {
        foreach (var u in plan.Units)
        {
            var expected = u.Assemblies
                .SelectMany(a => E.Assemblies.Single(x => x.Name == a).TypeFullNames)
                .OrderBy(t => t, StringComparer.Ordinal);
            Assert.Equal(expected, u.Types.OrderBy(t => t, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void MultiAssemblyUnitsAreGenuinelyStronglyConnected()
    {
        var g = MigrationPlanner.AssemblyGraph(E);
        foreach (var u in plan.Units.Where(u => u.Assemblies.Count > 1))
        {
            var sub = g.InducedOn(u.Assemblies);
            Assert.Single(Graphs.StronglyConnectedComponents(sub));
        }
    }

    [Fact]
    public void EveryKnotIsARealTypeLevelCycleNotJustASetOfTypes()
    {
        var tg = MigrationPlanner.TypeGraph(E);
        foreach (var knot in plan.Units.SelectMany(u => u.TypeKnots))
        {
            Assert.True(knot.Count > 1);
            Assert.Single(Graphs.StronglyConnectedComponents(tg.InducedOn(knot)));
        }
    }

    [Fact]
    public void APackagingArtefactIsAMultiAssemblyUnitWithNoTypeLevelKnotAtAll()
    {
        // This is the finding the whole project turns on. If it never happens here, the
        // corpus is not modelling the thing that makes real estates hard.
        var artefacts = plan.Units.Where(u => u.IsPackagingArtefact).ToList();
        Assert.NotEmpty(artefacts);
        Assert.All(artefacts, u => Assert.Empty(u.KnottedTypes));
    }

    [Fact]
    public void KnottedTypesAreAStrictSubsetOfTheirUnitsTypesForAtLeastOneUnit()
    {
        var entangled = plan.Units.Where(u => u.TypeKnots.Count > 0).ToList();
        Assert.NotEmpty(entangled);
        Assert.Contains(entangled, u => u.KnottedTypes.Count < u.Types.Count);
    }

    [Fact]
    public void UnitIdsAreUniqueAndStable()
    {
        Assert.Equal(plan.Units.Count, plan.Units.Select(u => u.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(plan.Units.Select(u => u.Id), MigrationPlanner.Build(E).Units.Select(u => u.Id));
    }

    // ---------- Condensation and waves ----------

    [Fact]
    public void TheCondensationIsAcyclicWhichIsTheWholeReasonForBuildingIt()
    {
        var c = MigrationPlanner.Condensation(E, plan.Units);
        Assert.All(Graphs.StronglyConnectedComponents(c), comp => Assert.Single(comp));
    }

    [Fact]
    public void TheRawAssemblyGraphIsNotAcyclicSoTheCondensationIsNotRedundant()
    {
        Assert.Throws<InvalidOperationException>(
            () => Graphs.TopologicalOrder(MigrationPlanner.AssemblyGraph(E)));
    }

    [Fact]
    public void EveryUnitHasAWave()
    {
        Assert.Equal(plan.Units.Count, plan.UnitWaves.Count);
        Assert.All(plan.Units, u => Assert.True(plan.UnitWaves.ContainsKey(u.Id)));
    }

    [Fact]
    public void EveryCrossUnitDependencyPointsFromALaterWaveToAnEarlierOne()
    {
        // If this fails the plan is not a plan, it is a list.
        var unitOf = plan.Units
            .SelectMany(u => u.Assemblies.Select(a => (a, u.Id)))
            .ToDictionary(x => x.a, x => x.Id, StringComparer.Ordinal);

        foreach (var e in E.AssemblyEdges)
        {
            var from = unitOf[e.From];
            var to = unitOf[e.To];
            if (from == to) continue;
            Assert.True(plan.UnitWaves[from] > plan.UnitWaves[to],
                $"{e.From}(w{plan.UnitWaves[from]}) -> {e.To}(w{plan.UnitWaves[to]})");
        }
    }

    [Fact]
    public void WavesStartAtOneAndLeaveNoGaps()
    {
        var used = plan.UnitWaves.Values.Distinct().OrderBy(x => x).ToList();
        Assert.Equal(1, used[0]);
        Assert.Equal(Enumerable.Range(1, used.Count), used);
    }

    [Fact]
    public void TheCriticalPathIsAsLongAsTheDeepestWave()
    {
        Assert.Equal(plan.UnitWaves.Values.Max(), plan.CriticalPath.Count);
    }

    [Fact]
    public void TheCriticalPathIsARealChainOfDependenciesInTheCondensation()
    {
        var c = MigrationPlanner.Condensation(E, plan.Units);
        for (var i = 0; i + 1 < plan.CriticalPath.Count; i++)
        {
            var from = plan.CriticalPath[i];
            var to = plan.CriticalPath[i + 1];
            Assert.Contains(c.Out(from), e => e.To == to);
        }
    }

    [Fact]
    public void TheCriticalPathDescendsOneWaveAtATime()
    {
        for (var i = 0; i + 1 < plan.CriticalPath.Count; i++)
            Assert.Equal(plan.UnitWaves[plan.CriticalPath[i]] - 1,
                         plan.UnitWaves[plan.CriticalPath[i + 1]]);
    }

    // ---------- Difficulty ----------

    [Fact]
    public void EveryAssemblyGetsADifficultyRow()
    {
        Assert.Equal(E.Assemblies.Count, plan.Difficulties.Count);
        Assert.Equal(E.Assemblies.Select(a => a.Name).OrderBy(x => x, StringComparer.Ordinal),
                     plan.Difficulties.Select(d => d.Assembly).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void UnitScoreIsTheSumOfTheOwnScoresOfEveryAssemblyInTheUnit()
    {
        var byName = plan.Difficulties.ToDictionary(d => d.Assembly, StringComparer.Ordinal);
        foreach (var u in plan.Units)
        {
            var sum = u.Assemblies.Sum(a => byName[a].OwnScore);
            Assert.All(u.Assemblies, a => Assert.Equal(sum, byName[a].UnitScore));
        }
    }

    [Fact]
    public void UnitScoreIsNeverLessThanOwnScore()
    {
        Assert.All(plan.Difficulties, d => Assert.True(d.UnitScore >= d.OwnScore));
    }

    [Fact]
    public void SomeAssemblyIsHarderThanItLooksWhichIsWhyUnitScoreExists()
    {
        Assert.Contains(plan.Difficulties, d => d.UnitScore > d.OwnScore);
    }

    [Fact]
    public void AnAssemblyIsMarkedUnbuildableExactlyWhenItHasNoSymbols()
    {
        var noPdb = E.Assemblies.Where(a => !a.SourceAvailable).Select(a => a.Name)
            .OrderBy(x => x, StringComparer.Ordinal);
        Assert.Equal(noPdb, plan.UnbuildableAssemblies);
    }

    [Fact]
    public void EveryUnbuildableAssemblyIsAlsoCountedAsConstrainedByOne()
    {
        // Reflexivity: if you cannot rebuild it, you are constrained by it.
        foreach (var a in plan.UnbuildableAssemblies)
            Assert.Contains(a, plan.ConstrainedByUnbuildable);
    }

    [Fact]
    public void TheConstrainedSetIsTheTransitiveClosureOfDependingOnSomethingUnbuildable()
    {
        var unbuildable = plan.UnbuildableAssemblies.ToHashSet(StringComparer.Ordinal);
        var g = MigrationPlanner.AssemblyGraph(E);
        var expected = new HashSet<string>(unbuildable, StringComparer.Ordinal);
        bool grew;
        do
        {
            grew = false;
            foreach (var n in g.Nodes)
                foreach (var e in g.Out(n))
                    if (expected.Contains(e.To) && expected.Add(n)) grew = true;
        } while (grew);

        Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal), plan.ConstrainedByUnbuildable);
    }

    [Fact]
    public void MoreAssembliesAreConstrainedByLostSourceThanActuallyLostIt()
    {
        Assert.True(plan.ConstrainedByUnbuildable.Count > plan.UnbuildableAssemblies.Count);
    }

    [Fact]
    public void TheWholePlanIsDeterministic()
    {
        var a = MigrationPlanner.Build(E);
        var b = MigrationPlanner.Build(E);
        Assert.Equal(a.CriticalPath, b.CriticalPath);
        Assert.Equal(a.Difficulties.Select(d => (d.Assembly, d.OwnScore, d.UnitScore)),
                     b.Difficulties.Select(d => (d.Assembly, d.OwnScore, d.UnitScore)));
    }

    // ---------- Kendall tau ----------

    [Fact]
    public void TauOfASequenceAgainstItselfIsOne()
    {
        Assert.Equal(1.0, MigrationPlanner.KendallTau([1, 2, 3, 4, 5], [1, 2, 3, 4, 5]), 9);
    }

    [Fact]
    public void TauOfASequenceAgainstItsReverseIsMinusOne()
    {
        Assert.Equal(-1.0, MigrationPlanner.KendallTau([1, 2, 3, 4, 5], [5, 4, 3, 2, 1]), 9);
    }

    [Fact]
    public void TauIsSymmetric()
    {
        double[] a = [3, 1, 4, 1, 5, 9, 2, 6];
        double[] b = [2, 7, 1, 8, 2, 8, 1, 8];
        Assert.Equal(MigrationPlanner.KendallTau(a, b), MigrationPlanner.KendallTau(b, a), 9);
    }

    [Fact]
    public void TauIsInvariantUnderAMonotoneRescalingOfEitherSide()
    {
        double[] a = [1, 2, 3, 4, 5, 6];
        double[] b = [2, 1, 4, 3, 6, 5];
        var scaled = b.Select(x => x * 10 + 3).ToArray();
        Assert.Equal(MigrationPlanner.KendallTau(a, b), MigrationPlanner.KendallTau(a, scaled), 9);
    }

    [Fact]
    public void TauMatchesAHandComputedValueWithOneDiscordantPair()
    {
        // 3 elements = 3 pairs. Swapping the last two makes exactly one discordant.
        Assert.Equal(1.0 / 3.0, MigrationPlanner.KendallTau([1, 2, 3], [1, 3, 2]), 9);
    }

    [Fact]
    public void TauRejectsMismatchedLengthsRatherThanTruncating()
    {
        Assert.Throws<ArgumentException>(() => MigrationPlanner.KendallTau([1, 2], [1, 2, 3]));
    }
}
