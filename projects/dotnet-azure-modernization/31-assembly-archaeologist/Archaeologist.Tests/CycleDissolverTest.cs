using Archaeologist.Core;

namespace Archaeologist.Tests;

/// <summary>
/// "These four assemblies have a cycle" is a fact nobody can act on. "Move this one type
/// and the cycle is gone" is a decision. The dissolver has to be right about that, and
/// minimal, or the advice is worse than silence.
/// </summary>
public class CycleDissolverTest : IClassFixture<EstateFixture>
{
    private readonly EstateFixture f;
    private Estate E => f.Estate;
    private readonly IReadOnlyList<MigrationUnit> units;

    public CycleDissolverTest(EstateFixture fixture)
    {
        f = fixture;
        units = MigrationPlanner.Units(E);
    }

    private IEnumerable<MigrationUnit> Cyclic => units.Where(u => u.Assemblies.Count > 1);

    private const string NewAsm = "Contoso.Extracted";

    /// <summary>Rebuild the assembly graph with the named types moved into a new assembly.</summary>
    private DiGraph AfterExtracting(IReadOnlyCollection<string> extracted)
    {
        var moved = extracted.ToHashSet(StringComparer.Ordinal);
        string Home(string t) => moved.Contains(t) ? NewAsm : E.AssemblyOfType(t);

        var nodes = E.Assemblies.Select(a => a.Name).Append(NewAsm);
        var edges = E.TypeEdges
            .Select(e => (From: Home(e.From), To: Home(e.To), e.Weight))
            .Where(e => e.From != e.To)
            .Select(e => new Edge(e.From, e.To, e.Weight));
        return new DiGraph(nodes, edges);
    }

    /// <summary>
    /// The unit is dissolved when the assemblies it used to contain -- plus wherever the
    /// extracted types now live -- no longer form any cycle. A cycle between the new
    /// assembly and an old one is just as much a build-order problem as the original.
    /// </summary>
    private bool StillCyclic(MigrationUnit u, IReadOnlyCollection<string> extracted) =>
        !IsAcyclic(AfterExtracting(extracted).InducedOn(u.Assemblies.Append(NewAsm)));

    private static bool IsAcyclic(DiGraph g) =>
        Graphs.StronglyConnectedComponents(g).All(c => c.Count == 1);

    [Fact]
    public void ASingleAssemblyUnitIsNotACycleAndNeedsNoWork()
    {
        foreach (var u in units.Where(u => u.Assemblies.Count == 1))
        {
            var d = CycleDissolver.Dissolve(E, u);
            Assert.True(d.Possible);
            Assert.Empty(d.TypesToExtract);
        }
    }

    [Fact]
    public void EveryCyclicUnitInThisEstateCanBeDissolved()
    {
        Assert.NotEmpty(Cyclic);
        Assert.All(Cyclic, u => Assert.True(CycleDissolver.Dissolve(E, u).Possible, u.Id));
    }

    [Fact]
    public void ExtractingTheProposedTypesReallyRemovesTheUnitFromTheAssemblyGraph()
    {
        // The claim is checked by rebuilding the graph, not by trusting the search.
        foreach (var u in Cyclic)
        {
            var d = CycleDissolver.Dissolve(E, u);
            Assert.False(StillCyclic(u, d.TypesToExtract),
                $"{u.Id} survived extracting {d.TypesToExtract.Count} types");
        }
    }

    [Fact]
    public void NoSmallerSetOfTypesWouldHaveWorkedWhichIsWhatMakesItAdvice()
    {
        foreach (var u in Cyclic)
        {
            var d = CycleDissolver.Dissolve(E, u);
            if (d.TypesToExtract.Count == 0) continue;

            foreach (var smaller in Subsets(d.TypesToExtract, d.TypesToExtract.Count - 1))
                Assert.True(StillCyclic(u, smaller),
                    $"{u.Id}: {string.Join(",", smaller)} would also have worked, so the answer is not minimal");
        }
    }

    private static IEnumerable<List<string>> Subsets(IReadOnlyList<string> items, int size)
    {
        if (size == 0) { yield return []; yield break; }
        for (var i = 0; i <= items.Count - size; i++)
            foreach (var rest in Subsets(items.Skip(i + 1).ToList(), size - 1))
                yield return new List<string> { items[i] }.Concat(rest).ToList();
    }

    [Fact]
    public void ExtractedTypesAlwaysComeFromInsideTheUnit()
    {
        foreach (var u in Cyclic)
        {
            var inUnit = u.Types.ToHashSet(StringComparer.Ordinal);
            Assert.All(CycleDissolver.Dissolve(E, u).TypesToExtract, t => Assert.Contains(t, inUnit));
        }
    }

    [Fact]
    public void APackagingOnlyCycleDissolvesByMovingTypesWithoutChangingAnyCode()
    {
        // The best possible finding: the cycle was a packaging decision, not a design flaw.
        var artefacts = units.Where(u => u.IsPackagingArtefact).ToList();
        Assert.NotEmpty(artefacts);
        foreach (var u in artefacts)
        {
            var d = CycleDissolver.Dissolve(E, u);
            Assert.True(d.Possible);
            Assert.False(d.QuarantinesAKnot);
            Assert.NotEmpty(d.TypesToExtract);
        }
    }

    [Fact]
    public void AGenuinelyEntangledUnitIsResolvedByQuarantiningTheKnotRatherThanDeclaringDefeat()
    {
        // A cycle inside one assembly is not a build-order problem, so the honest answer
        // for a real knot is "put the knot in one place", not "impossible".
        var entangled = units.Where(u => u.TypeKnots.Count > 0).ToList();
        Assert.NotEmpty(entangled);
        foreach (var u in entangled)
        {
            var d = CycleDissolver.Dissolve(E, u);
            Assert.True(d.Possible);
            Assert.True(d.QuarantinesAKnot, $"{u.Id} claimed to dissolve a real knot without quarantining it");
        }
    }

    [Fact]
    public void QuarantineIsReportedOnlyWhenAKnottedTypeIsActuallyMoved()
    {
        foreach (var u in Cyclic)
        {
            var d = CycleDissolver.Dissolve(E, u);
            var knotted = u.KnottedTypes.ToHashSet(StringComparer.Ordinal);
            Assert.Equal(d.TypesToExtract.Any(knotted.Contains), d.QuarantinesAKnot);
        }
    }

    [Fact]
    public void TheCheapestDissolutionInThisEstateIsASingleTypeWithNoCodeChange()
    {
        var best = Cyclic.Select(u => CycleDissolver.Dissolve(E, u))
            .Where(d => d.TypesToExtract.Count > 0)
            .Min(d => d.TypesToExtract.Count);
        Assert.Equal(1, best);
    }

    [Fact]
    public void AUnitLargerThanTheSearchLimitIsRefusedWithAReasonNotGuessedAt()
    {
        var huge = new MigrationUnit("synthetic",
            ["A", "B"],
            Enumerable.Range(0, CycleDissolver.MaxTypesForExactSearch + 1).Select(i => $"T{i}").ToList(),
            []);
        var d = CycleDissolver.Dissolve(E, huge);
        Assert.False(d.Possible);
        Assert.Contains(CycleDissolver.MaxTypesForExactSearch.ToString(), d.Reason);
    }

    [Fact]
    public void EveryDissolutionCarriesTheIdOfTheUnitItDescribes()
    {
        Assert.All(units, u => Assert.Equal(u.Id, CycleDissolver.Dissolve(E, u).UnitId));
    }

    [Fact]
    public void EveryDissolutionGivesAReason()
    {
        Assert.All(units, u => Assert.False(string.IsNullOrWhiteSpace(CycleDissolver.Dissolve(E, u).Reason)));
    }

    [Fact]
    public void ExtractedTypeListsAreSortedAndDeterministic()
    {
        foreach (var u in Cyclic)
        {
            var a = CycleDissolver.Dissolve(E, u).TypesToExtract;
            Assert.Equal(a.OrderBy(t => t, StringComparer.Ordinal), a);
            Assert.Equal(a, CycleDissolver.Dissolve(E, u).TypesToExtract);
        }
    }
}
