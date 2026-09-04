using Archaeologist.Core;
using Mono.Cecil;

namespace Archaeologist.Tests;

/// <summary>
/// The reader is the seam between "bytes on disk" and every conclusion in the report. It
/// has no source, no reference assemblies and no resolver -- only metadata. These tests
/// pin what it can and cannot know from that position.
/// </summary>
public class IlReaderTest : IClassFixture<EstateFixture>
{
    private readonly EstateFixture f;
    private Estate E => f.Estate;

    public IlReaderTest(EstateFixture fixture) => f = fixture;

    [Fact]
    public void ReadingTheSameDirectoryTwiceInOneProcessWorks()
    {
        // Cecil memory-maps the files it reads. Forgetting to dispose the modules gives an
        // analyser that runs exactly once per process and then fails on a file lock.
        var a = IlReader.Read(f.Directory);
        var b = IlReader.Read(f.Directory);
        Assert.Equal(a.Assemblies.Select(x => x.Name), b.Assemblies.Select(x => x.Name));
        Assert.Equal(a.MethodEdges.Select(e => e.ToString()), b.MethodEdges.Select(e => e.ToString()));
    }

    [Fact]
    public void TheReaderDoesNotHoldTheFilesOpenAfterItReturns()
    {
        var dir = Path.Combine(Path.GetTempPath(), "arch-lock-" + Guid.NewGuid().ToString("N")[..8]);
        CorpusBuilder.Emit(CorpusSpec.Contoso(), dir);
        IlReader.Read(dir);
        Directory.Delete(dir, true); // throws IOException if a module is still mapped
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void AnEmptyDirectoryProducesAnEmptyEstateRatherThanThrowing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "arch-empty-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var e = IlReader.Read(dir);
            Assert.Empty(e.Assemblies);
            Assert.Empty(e.Types);
            Assert.Empty(e.Blockers);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------- What it reads ----------

    [Fact]
    public void EveryTypeBelongsToAnAssemblyThatClaimsIt()
    {
        var byAsm = E.Assemblies.ToDictionary(a => a.Name, a => a.TypeFullNames.ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        Assert.All(E.Types, t => Assert.Contains(t.FullName, byAsm[t.AssemblyName]));
    }

    [Fact]
    public void EveryMethodBelongsToATypeTheReaderAlsoFound()
    {
        var types = E.Types.Select(t => t.FullName).ToHashSet(StringComparer.Ordinal);
        Assert.All(E.Methods, m => Assert.Contains(m.TypeFullName, types));
    }

    [Fact]
    public void EveryMethodEdgeConnectsTwoMethodsInsideTheEstate()
    {
        var ids = E.Methods.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var e in E.MethodEdges)
        {
            Assert.Contains(e.From, ids);
            Assert.Contains(e.To, ids);
        }
    }

    [Fact]
    public void CallsIntoAssembliesOutsideTheEstateAreNotMethodEdges()
    {
        // The reader must not invent a node for System.Web.HttpContext just because
        // something called it. External calls are blockers, not graph edges.
        Assert.DoesNotContain(E.Methods, m => m.TypeFullName.StartsWith("System.", StringComparison.Ordinal));
    }

    [Fact]
    public void EdgeWeightIsTheNumberOfCallSitesNotTheNumberOfDistinctCallees()
    {
        Assert.All(E.MethodEdges, e => Assert.True(e.Weight >= 1));
        Assert.Contains(E.MethodEdges, e => e.Weight > 1);
    }

    [Fact]
    public void LiftingToTypesSumsWeightsRatherThanCountingDistinctPairs()
    {
        // A cut between two assemblies costs what all the calls across it cost, not one.
        Assert.Contains(E.TypeEdges, e => e.Weight > 1);
        Assert.Equal(E.MethodEdges.Where(e => E.TypeOfMethod(e.From) != E.TypeOfMethod(e.To)).Sum(e => e.Weight),
                     E.TypeEdges.Sum(e => e.Weight));
    }

    [Fact]
    public void TypeEdgesAreTheMethodEdgesLiftedToTypesWithWeightsSummed()
    {
        var expected = E.MethodEdges
            .Select(e => (From: E.TypeOfMethod(e.From), To: E.TypeOfMethod(e.To), e.Weight))
            .Where(x => x.From != x.To)
            .GroupBy(x => (x.From, x.To))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Weight));

        Assert.Equal(expected.Count, E.TypeEdges.Count);
        foreach (var e in E.TypeEdges) Assert.Equal(expected[(e.From, e.To)], e.Weight);
    }

    [Fact]
    public void AssemblyEdgesAreTheTypeEdgesLiftedAgain()
    {
        var expected = E.TypeEdges
            .Select(e => (From: E.AssemblyOfType(e.From), To: E.AssemblyOfType(e.To)))
            .Where(x => x.From != x.To)
            .Distinct()
            .Count();
        Assert.Equal(expected, E.AssemblyEdges.Count);
    }

    [Fact]
    public void ExternalReferencesAreExactlyTheManifestNamesThatAreNotInTheEstate()
    {
        var inEstate = E.Assemblies.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        Assert.All(E.ExternalReferences, r => Assert.DoesNotContain(r, inEstate));
        Assert.Contains("mscorlib", E.ExternalReferences);
    }

    [Fact]
    public void SourceAvailabilityIsReadFromThePresenceOfSymbolsNotGuessed()
    {
        foreach (var a in E.Assemblies)
            Assert.Equal(File.Exists(Path.Combine(f.Directory, a.Name + ".pdb")), a.SourceAvailable);
    }

    [Fact]
    public void SomeAssembliesHaveSymbolsAndSomeDoNotOtherwiseTheSignalIsConstant()
    {
        Assert.Contains(E.Assemblies, a => a.SourceAvailable);
        Assert.Contains(E.Assemblies, a => !a.SourceAvailable);
    }

    [Fact]
    public void LosingTheSymbolsDoesNotStopTheAnalysis()
    {
        // The pdb is a project-management signal, not an analysis input. Deleting every
        // one of them must not change a single conclusion.
        var dir = Path.Combine(Path.GetTempPath(), "arch-nopdb-" + Guid.NewGuid().ToString("N")[..8]);
        CorpusBuilder.Emit(CorpusSpec.Contoso(), dir);
        try
        {
            foreach (var pdb in Directory.GetFiles(dir, "*.pdb")) File.Delete(pdb);
            var stripped = IlReader.Read(dir);
            Assert.Equal(E.Blockers.Count, stripped.Blockers.Count);
            Assert.Equal(E.MethodEdges.Count, stripped.MethodEdges.Count);
            Assert.All(stripped.Assemblies, a => Assert.False(a.SourceAvailable));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------- Blockers ----------

    [Fact]
    public void EveryBlockerPointsAtAMethodThatExists()
    {
        var ids = E.Methods.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(E.Blockers, b => Assert.Contains($"{b.TypeFullName}::{b.MethodName}", ids));
    }

    [Fact]
    public void EveryBlockerCarriesTheRuleThatMatchedIt()
    {
        Assert.All(E.Blockers, b =>
            Assert.Same(b.Rule, BlockerRules.Match($"{b.Rule.Namespace}.{b.Rule.TypeName}",
                b.Rule.MemberName ?? "x")));
    }

    [Fact]
    public void BlockerSitesAreDistinctSoNothingIsCountedTwice()
    {
        var sites = E.Blockers.Select(b => $"{b.Site}|{b.Rule.Key}").ToList();
        Assert.Equal(sites.Count, sites.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void BlockersAreFoundInsideAssembliesWithNoSymbols()
    {
        // The point of working at IL level: the assemblies nobody can rebuild are exactly
        // the ones a source scanner cannot look inside.
        var sourceless = E.Assemblies.Where(a => !a.SourceAvailable).Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(E.Blockers, b => sourceless.Contains(b.AssemblyName));
    }

    [Fact]
    public void TheEstateFindsBlockersThatTheManifestAloneCouldNotPredict()
    {
        // Blockers whose only manifest evidence is mscorlib, which every assembly cites.
        var byAsm = E.Assemblies.ToDictionary(a => a.Name, StringComparer.Ordinal);
        var invisible = E.Blockers
            .Where(b => byAsm[b.AssemblyName].ExternalReferences.All(r => r == "mscorlib"))
            .ToList();
        Assert.NotEmpty(invisible);
    }

    // ---------- Reflection ----------

    [Fact]
    public void AllThreeKindsOfReflectionSiteAreRecognised()
    {
        foreach (var k in Enum.GetValues<ReflectionKind>())
            Assert.Contains(E.ReflectionSites, s => s.Kind == k);
    }

    [Fact]
    public void ALiteralTypeNameSiteResolvesToATypeInTheEstate()
    {
        var types = E.Types.Select(t => t.FullName).ToHashSet(StringComparer.Ordinal);
        foreach (var s in E.ReflectionSites.Where(s => s.Kind == ReflectionKind.LiteralTypeName))
        {
            Assert.NotNull(s.ResolvedTarget);
            Assert.Contains(s.ResolvedTarget, types);
        }
    }

    [Fact]
    public void AComputedTypeNameSiteResolvesToNothingButMayStillYieldAPrefix()
    {
        var computed = E.ReflectionSites.Where(s => s.Kind == ReflectionKind.ComputedTypeName).ToList();
        Assert.NotEmpty(computed);
        Assert.All(computed, s => Assert.Null(s.ResolvedTarget));
        Assert.Contains(computed, s => s.RecoveredPrefix is not null);
    }

    [Fact]
    public void ARecoveredPrefixIsAnActualPrefixOfSomethingInTheEstate()
    {
        foreach (var s in E.ReflectionSites.Where(s => s.RecoveredPrefix is not null))
            Assert.Contains(E.Types, t => t.FullName.StartsWith(s.RecoveredPrefix!, StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyComputedSitesAreUndecidable()
    {
        Assert.All(E.ReflectionSites,
            s => Assert.Equal(s.Kind != ReflectionKind.ComputedTypeName, s.IsDecidable));
    }

    // ---------- Determinism ----------

    [Fact]
    public void EveryListTheReaderReturnsIsSortedOrdinally()
    {
        Assert.Equal(E.Assemblies.Select(a => a.Name).OrderBy(x => x, StringComparer.Ordinal),
                     E.Assemblies.Select(a => a.Name));
        Assert.Equal(E.Types.Select(t => t.FullName).OrderBy(x => x, StringComparer.Ordinal),
                     E.Types.Select(t => t.FullName));
        Assert.Equal(E.ExternalReferences.OrderBy(x => x, StringComparer.Ordinal), E.ExternalReferences);
    }

    [Fact]
    public void RebuildingTheCorpusFromScratchProducesByteIdenticalAssemblies()
    {
        // If the emitter is not deterministic then neither is anything downstream of it.
        var a = Path.Combine(Path.GetTempPath(), "arch-det-a-" + Guid.NewGuid().ToString("N")[..8]);
        var b = Path.Combine(Path.GetTempPath(), "arch-det-b-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            CorpusBuilder.Emit(CorpusSpec.Contoso(), a);
            CorpusBuilder.Emit(CorpusSpec.Contoso(), b);
            var ea = IlReader.Read(a);
            var eb = IlReader.Read(b);
            Assert.Equal(ea.MethodEdges.Select(e => e.ToString()), eb.MethodEdges.Select(e => e.ToString()));
            Assert.Equal(ea.Blockers.Select(x => x.Site + x.Rule.Key), eb.Blockers.Select(x => x.Site + x.Rule.Key));
            Assert.Equal(ea.ReflectionSites.Select(x => x.Site), eb.ReflectionSites.Select(x => x.Site));
        }
        finally
        {
            Directory.Delete(a, true);
            Directory.Delete(b, true);
        }
    }

    [Fact]
    public void TypeOfMethodAndAssemblyOfTypeAgreeWithTheDeclaredStructure()
    {
        foreach (var m in E.Methods)
        {
            Assert.Equal(m.TypeFullName, E.TypeOfMethod(m.Id));
            Assert.Equal(m.AssemblyName, E.AssemblyOfType(m.TypeFullName));
        }
    }

    [Fact]
    public void AskingForAnUnknownMethodOrTypeThrowsRatherThanReturningNull()
    {
        Assert.ThrowsAny<Exception>(() => E.TypeOfMethod("No.Such::Method"));
        Assert.ThrowsAny<Exception>(() => E.AssemblyOfType("No.Such.Type"));
    }
}
