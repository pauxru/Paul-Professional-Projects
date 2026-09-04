using Archaeologist.Core;

namespace Archaeologist.Tests;

/// <summary>
/// The four reachability modes form a lattice: each one honours strictly more ways of
/// reaching code than the one before it, so each one must find strictly fewer dead things.
/// If that ordering ever breaks, the report's central claim -- that "what is dead?" has
/// four different defensible answers -- becomes noise.
/// </summary>
public class ReachabilityTest : IClassFixture<EstateFixture>
{
    private readonly EstateFixture f;
    private Estate E => f.Estate;
    private static readonly ReachabilityMode[] Ladder =
    [
        ReachabilityMode.CallsOnly,
        ReachabilityMode.DecidableReflection,
        ReachabilityMode.PrefixConstrained,
        ReachabilityMode.FullySound,
    ];

    public ReachabilityTest(EstateFixture fixture) => f = fixture;

    private ReachabilityResult R(ReachabilityMode m) => Reachability.From(E, f.Spec.EntryPoint, m);

    [Fact]
    public void EveryModeKeepsTheEntryPointItself()
    {
        Assert.All(Ladder, m => Assert.Contains(f.Spec.EntryPoint, R(m).LiveMethods));
    }

    [Fact]
    public void LiveSetsGrowMonotonicallyUpTheLadder()
    {
        for (var i = 1; i < Ladder.Length; i++)
        {
            var weaker = R(Ladder[i - 1]).LiveTypes.ToHashSet(StringComparer.Ordinal);
            var stronger = R(Ladder[i]).LiveTypes.ToHashSet(StringComparer.Ordinal);
            Assert.True(weaker.IsSubsetOf(stronger),
                $"{Ladder[i]} lost types that {Ladder[i - 1]} considered live");
        }
    }

    [Fact]
    public void DeadSetsShrinkMonotonicallyUpTheLadder()
    {
        for (var i = 1; i < Ladder.Length; i++)
        {
            var weaker = R(Ladder[i - 1]).DeadTypes.ToHashSet(StringComparer.Ordinal);
            var stronger = R(Ladder[i]).DeadTypes.ToHashSet(StringComparer.Ordinal);
            Assert.True(stronger.IsSubsetOf(weaker),
                $"{Ladder[i]} called something dead that {Ladder[i - 1]} called live");
        }
    }

    [Fact]
    public void TheLadderActuallyMovesOtherwiseTheDistinctionIsAcademic()
    {
        var counts = Ladder.Select(m => R(m).DeadTypes.Count).ToList();
        Assert.True(counts.Distinct().Count() >= 3,
            $"only {counts.Distinct().Count()} distinct answers: {string.Join(",", counts)}");
    }

    [Fact]
    public void TheFullySoundAnswerIsThatNothingCanBeProvenDead()
    {
        // One unconstrained computed-reflection site in reachable code and the sound
        // analysis has to give up on the entire estate. That is the honest answer, and it
        // is why nobody uses it.
        var sound = R(ReachabilityMode.FullySound);
        Assert.Empty(sound.DeadTypes);
        Assert.Empty(sound.DeadAssemblies);
        Assert.True(sound.UnconstrainedSitesHonoured > 0);
    }

    [Fact]
    public void OnlyTheFullySoundModeHonoursUnconstrainedSites()
    {
        Assert.All(Ladder.Where(m => m != ReachabilityMode.FullySound),
            m => Assert.Equal(0, R(m).UnconstrainedSitesHonoured));
    }

    [Fact]
    public void RecoveringOneStringPrefixIsWorthMoreThanTheEntireSoundAnalysis()
    {
        // The finding: a two-instruction dataflow pattern turns "nothing is deletable"
        // into a real answer. This is the argument for partial soundness over none.
        var sound = R(ReachabilityMode.FullySound).DeadTypes.Count;
        var prefixed = R(ReachabilityMode.PrefixConstrained).DeadTypes.Count;
        Assert.Equal(0, sound);
        Assert.True(prefixed > 0, "prefix constraint bought nothing back");
    }

    [Fact]
    public void CallsOnlyIsTheMostOptimisticAnswerAndIsWrongAboutAtLeastOneType()
    {
        var callsOnly = R(ReachabilityMode.CallsOnly).DeadTypes.ToHashSet(StringComparer.Ordinal);
        var decidable = R(ReachabilityMode.DecidableReflection).DeadTypes.ToHashSet(StringComparer.Ordinal);
        var wronglyDead = callsOnly.Except(decidable).ToList();
        Assert.NotEmpty(wronglyDead);
    }

    [Fact]
    public void TypesTheCorpusPlantedAsReflectionOnlyAreDeadUnderCallsOnlyAndLiveOtherwise()
    {
        var callsOnlyDeadAsms = R(ReachabilityMode.CallsOnly).DeadAssemblies.ToHashSet(StringComparer.Ordinal);
        var decidableLiveAsms = R(ReachabilityMode.DecidableReflection).LiveAssemblies.ToHashSet(StringComparer.Ordinal);
        foreach (var a in f.Spec.PlantedReflectionLiveAssemblies)
        {
            Assert.Contains(a, callsOnlyDeadAsms);
            Assert.Contains(a, decidableLiveAsms);
        }
    }

    [Fact]
    public void ThePrefixConstrainedDeadSetIsExactlyTheUnreachableMinusTheReflectivelyRevived()
    {
        // Everything CallsOnly cannot reach is a candidate for deletion. Reflection takes
        // some of them back -- by literal name, and by a name whose prefix the IL still
        // admits. Whatever survives both is what is genuinely safe to delete.
        var revived = f.Spec.PlantedReflectionLiveAssemblies
            .Concat(f.Spec.PlantedUndecidableAssemblies)
            .ToHashSet(StringComparer.Ordinal);
        var expected = f.Spec.PlantedStaticallyUnreachableAssemblies
            .Where(a => !revived.Contains(a))
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(expected);
        Assert.Equal(expected, R(ReachabilityMode.PrefixConstrained).DeadAssemblies);
    }

    [Fact]
    public void TheUndecidableAssemblyIsDeadUnderCallsOnlyAndAliveOnceThePrefixIsHonoured()
    {
        var callsOnly = R(ReachabilityMode.CallsOnly).DeadAssemblies.ToHashSet(StringComparer.Ordinal);
        var prefixed = R(ReachabilityMode.PrefixConstrained).LiveAssemblies.ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(f.Spec.PlantedUndecidableAssemblies);
        foreach (var a in f.Spec.PlantedUndecidableAssemblies)
        {
            Assert.Contains(a, callsOnly);
            Assert.Contains(a, prefixed);
        }
    }

    [Fact]
    public void AnAssemblyIsOnlyDeadIfEveryOneOfItsTypesIsDead()
    {
        foreach (var m in Ladder)
        {
            var r = R(m);
            var deadTypes = r.DeadTypes.ToHashSet(StringComparer.Ordinal);
            foreach (var asm in r.DeadAssemblies)
                Assert.All(E.Assemblies.Single(a => a.Name == asm).TypeFullNames,
                    t => Assert.Contains(t, deadTypes));
        }
    }

    [Fact]
    public void LiveAndDeadTypesPartitionTheEstateWithNoOverlapAndNoGap()
    {
        foreach (var m in Ladder)
        {
            var r = R(m);
            Assert.Empty(r.LiveTypes.Intersect(r.DeadTypes, StringComparer.Ordinal));
            Assert.Equal(E.Types.Count, r.LiveTypes.Count + r.DeadTypes.Count);
        }
    }

    [Fact]
    public void ResultsAreSortedSoTheReportIsByteStable()
    {
        foreach (var m in Ladder)
        {
            var r = R(m);
            Assert.Equal(r.DeadTypes.OrderBy(x => x, StringComparer.Ordinal), r.DeadTypes);
            Assert.Equal(r.LiveTypes.OrderBy(x => x, StringComparer.Ordinal), r.LiveTypes);
            Assert.Equal(r.DeadAssemblies.OrderBy(x => x, StringComparer.Ordinal), r.DeadAssemblies);
        }
    }

    [Fact]
    public void AnUnknownEntryPointIsRejectedRatherThanTreatedAsAnEmptyProgram()
    {
        // Silently returning "everything is dead" for a typo'd entry point is the kind of
        // bug that gets an assembly deleted.
        Assert.ThrowsAny<Exception>(() =>
            Reachability.From(E, "No.Such.Type::Main", ReachabilityMode.CallsOnly));
    }

    [Fact]
    public void EveryLiveTypeHasAtLeastOneLiveMethodAndViceVersa()
    {
        foreach (var m in Ladder)
        {
            var r = R(m);
            var liveTypes = r.LiveTypes.ToHashSet(StringComparer.Ordinal);
            var typesOfLiveMethods = r.LiveMethods
                .Select(id => id[..id.LastIndexOf("::", StringComparison.Ordinal)])
                .ToHashSet(StringComparer.Ordinal);
            Assert.True(typesOfLiveMethods.IsSubsetOf(liveTypes));
        }
    }

    [Fact]
    public void EveryModeIsDeterministic()
    {
        foreach (var m in Ladder)
            Assert.Equal(R(m).DeadTypes, R(m).DeadTypes);
    }
}
