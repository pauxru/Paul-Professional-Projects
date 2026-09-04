using Archaeologist.Core;
using Mono.Cecil;

namespace Archaeologist.Tests;

/// <summary>
/// The corpus declares what it planted. These tests do not take its word for it: every
/// claim is re-derived from the emitted IL by machinery that has never seen the spec.
/// A corpus that marks its own homework proves nothing about the analyser.
/// </summary>
[Collection("estate")]
public sealed class CorpusIntegrityTest(EstateFixture f)
{
    private Estate E => f.Estate;
    private CorpusSpec Spec => f.Spec;

    [Fact]
    public void EveryCallTargetNamedInTheSpecExists()
    {
        var declared = Spec.AllTypes.SelectMany(t => t.Methods.Select(m => $"{t.FullName}::{m.Name}"))
            .ToHashSet(StringComparer.Ordinal);
        var referenced = Spec.AllTypes.SelectMany(t => t.Methods).SelectMany(m => m.Body)
            .OfType<CallCorpus>().Select(c => $"{c.TypeFullName}::{c.Method}").Distinct().ToList();

        var dangling = referenced.Where(x => !declared.Contains(x)).ToList();
        Assert.Empty(dangling);
    }

    [Fact]
    public void EveryTypeNamedByReflectionInTheSpecExists()
    {
        var types = Spec.AllTypes.Select(t => t.FullName).ToHashSet(StringComparer.Ordinal);
        var named = Spec.AllTypes.SelectMany(t => t.Methods).SelectMany(m => m.Body)
            .SelectMany(op => op switch
            {
                GetTypeLiteral g => new[] { g.TypeName },
                ActivatorTypeOf a => [a.TypeFullName],
                _ => Array.Empty<string>(),
            }).ToList();

        Assert.NotEmpty(named);
        Assert.All(named, n => Assert.Contains(n, types));
    }

    [Fact]
    public void TheEntryPointExists() =>
        Assert.Contains(Spec.EntryPoint, E.Methods.Select(m => m.Id));

    [Fact]
    public void EveryPlantedKnotIsAGenuineTypeLevelCycle()
    {
        var typeGraph = MigrationPlanner.TypeGraph(E);
        var found = Graphs.StronglyConnectedComponents(typeGraph)
            .Where(c => c.Count > 1)
            .Select(c => string.Join("|", c.OrderBy(x => x, StringComparer.Ordinal)))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var knot in Spec.PlantedTypeLevelKnots)
            Assert.Contains(string.Join("|", knot.OrderBy(x => x, StringComparer.Ordinal)), found);
    }

    [Fact]
    public void ThereAreNoTypeLevelCyclesBeyondThePlantedOnes()
    {
        var typeGraph = MigrationPlanner.TypeGraph(E);
        var found = Graphs.StronglyConnectedComponents(typeGraph).Count(c => c.Count > 1);
        Assert.Equal(Spec.PlantedTypeLevelKnots.Count, found);
    }

    [Fact]
    public void EveryPlantedPackagingOnlyCycleIsACycleOfAssemblies()
    {
        var units = MigrationPlanner.Units(E);
        foreach (var cycle in Spec.PlantedPackagingOnlyCycles)
        {
            var unit = units.Single(u => u.Assemblies.Contains(cycle[0]));
            foreach (var a in cycle) Assert.Contains(a, unit.Assemblies);
            Assert.True(unit.Assemblies.Count > 1);
        }
    }

    [Fact]
    public void EveryPlantedPackagingOnlyCycleHasNoTypeLevelCycle()
    {
        var units = MigrationPlanner.Units(E);
        foreach (var cycle in Spec.PlantedPackagingOnlyCycles)
        {
            var unit = units.Single(u => u.Assemblies.Contains(cycle[0]));
            Assert.Empty(unit.TypeKnots);
        }
    }

    [Fact]
    public void PlantedUnreachableAssembliesAreUnreachableByCalls()
    {
        var r = Reachability.From(E, Spec.EntryPoint, ReachabilityMode.CallsOnly);
        foreach (var a in Spec.PlantedStaticallyUnreachableAssemblies)
            Assert.Contains(a, r.DeadAssemblies);
    }

    [Fact]
    public void NothingElseIsUnreachableByCalls()
    {
        var r = Reachability.From(E, Spec.EntryPoint, ReachabilityMode.CallsOnly);
        Assert.Equal(
            Spec.PlantedStaticallyUnreachableAssemblies.OrderBy(x => x, StringComparer.Ordinal),
            r.DeadAssemblies.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void PlantedReflectionLiveAssembliesComeBackToLifeUnderDecidableReflection()
    {
        var r = Reachability.From(E, Spec.EntryPoint, ReachabilityMode.DecidableReflection);
        foreach (var a in Spec.PlantedReflectionLiveAssemblies)
            Assert.DoesNotContain(a, r.DeadAssemblies);
    }

    [Fact]
    public void PlantedUndecidableAssembliesStayDeadUnderDecidableReflection()
    {
        var r = Reachability.From(E, Spec.EntryPoint, ReachabilityMode.DecidableReflection);
        foreach (var a in Spec.PlantedUndecidableAssemblies)
            Assert.Contains(a, r.DeadAssemblies);
    }

    [Fact]
    public void PlantedUndecidableAssembliesComeBackUnderPrefixConstraint()
    {
        var r = Reachability.From(E, Spec.EntryPoint, ReachabilityMode.PrefixConstrained);
        foreach (var a in Spec.PlantedUndecidableAssemblies)
            Assert.DoesNotContain(a, r.DeadAssemblies);
    }

    [Fact]
    public void TheOnlyThingStillDeleteableUnderTheSoundPrefixAnalysisIsTheAbandonedMigrationTool()
    {
        var r = Reachability.From(E, Spec.EntryPoint, ReachabilityMode.PrefixConstrained);
        Assert.Equal(["Contoso.Claims.Migration.Tools"], r.DeadAssemblies);
    }

    [Fact]
    public void AssembliesDeclaredWithoutSourceHaveNoSymbols()
    {
        var declared = Spec.Assemblies.Where(a => !a.SourceAvailable).Select(a => a.Name)
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        var observed = E.Assemblies.Where(a => !a.SourceAvailable).Select(a => a.Name)
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(declared, observed);
        Assert.NotEmpty(declared);
    }

    [Fact]
    public void AssembliesDeclaredWithSourceDoHaveSymbols()
    {
        foreach (var a in Spec.Assemblies.Where(a => a.SourceAvailable))
            Assert.True(File.Exists(Path.Combine(f.Directory, a.Name + ".pdb")), a.Name);
    }

    /// <summary>
    /// The premise of the whole project, and it is worse than "the assemblies are missing".
    /// Some of them resolve. On a modern machine System.Web, System.Data, System.Drawing and
    /// System.Configuration all resolve happily -- to empty type-forwarding facades that
    /// contain none of the types this estate actually calls. An analyser that resolved its
    /// references would not fail loudly; it would succeed quietly and be wrong. That is why
    /// the reader never resolves anything.
    /// </summary>
    [Fact]
    public void ResolvingTheFrameworkReferencesWouldMisleadRatherThanFail()
    {
        var resolver = new DefaultAssemblyResolver();
        var absent = new List<string>();
        var resolvedButEmpty = new List<string>();

        // What types does the estate actually name against each external assembly?
        // Read it straight out of the TypeRef table rather than trusting any model.
        var wantedByAssembly = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var file in System.IO.Directory.GetFiles(f.Directory, "*.dll"))
        {
            using var m = ModuleDefinition.ReadModule(file);
            foreach (var tr in m.GetTypeReferences())
                if (tr.Scope is AssemblyNameReference an)
                    (wantedByAssembly.TryGetValue(an.Name, out var s)
                        ? s : wantedByAssembly[an.Name] = new HashSet<string>(StringComparer.Ordinal))
                        .Add(tr.FullName);
        }

        foreach (var name in E.ExternalReferences.Where(n => n != "mscorlib"))
        {
            AssemblyDefinition asm;
            try { asm = resolver.Resolve(new AssemblyNameReference(name, new Version(4, 0, 0, 0))); }
            catch (AssemblyResolutionException) { absent.Add(name); continue; }

            // It resolved. Does it contain the types the estate names against it? No.
            var wanted = wantedByAssembly[name];
            Assert.NotEmpty(wanted);
            var present = asm.MainModule.GetTypes().Select(t => t.FullName).ToHashSet(StringComparer.Ordinal);
            Assert.DoesNotContain(wanted, w => present.Contains(w));
            resolvedButEmpty.Add(name);
        }

        // Both failure modes are represented, so the design decision is not driven by one of them.
        Assert.NotEmpty(absent);
        Assert.NotEmpty(resolvedButEmpty);
        Assert.Equal(E.ExternalReferences.Count(n => n != "mscorlib"), absent.Count + resolvedButEmpty.Count);
    }

    /// <summary>
    /// The reader must not merely tolerate a hostile resolver, it must never call one.
    /// Any resolution attempt is a latent dependency on the analysis machine's SDK layout.
    /// </summary>
    [Fact]
    public void TheReaderNeverAsksToResolveAnything()
    {
        var spy = new RecordingResolver();
        var estate = IlReader.Read(f.Directory, spy);
        Assert.Equal(E.Assemblies.Count, estate.Assemblies.Count);
        Assert.Empty(spy.Requested);
    }

    private sealed class RecordingResolver : IAssemblyResolver
    {
        public List<string> Requested { get; } = new();
        public AssemblyDefinition Resolve(AssemblyNameReference name)
        {
            Requested.Add(name.Name);
            throw new AssemblyResolutionException(name);
        }
        public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters p) => Resolve(name);
        public void Dispose() { }
    }

    [Fact]
    public void EveryEmittedAssemblyDeclaresTheFrameworkEraInItsManifest()
    {
        foreach (var file in Directory.GetFiles(f.Directory, "*.dll"))
        {
            using var m = ModuleDefinition.ReadModule(file);
            Assert.All(m.AssemblyReferences.Where(r => r.Name == "mscorlib"),
                r => Assert.Equal(new Version(4, 0, 0, 0), r.Version));
        }
    }

    [Fact]
    public void TheReferenceToSystemWebCarriesTheRealPublicKeyToken()
    {
        using var m = ModuleDefinition.ReadModule(Path.Combine(f.Directory, "Contoso.Claims.Web.dll"));
        var sysWeb = m.AssemblyReferences.Single(r => r.Name == "System.Web");
        Assert.Equal("b03f5f7f11d50a3a", Convert.ToHexString(sysWeb.PublicKeyToken).ToLowerInvariant());
    }

    [Fact]
    public void EveryAssemblyInTheSpecWasEmittedAndRead()
    {
        Assert.Equal(
            Spec.Assemblies.Select(a => a.Name).OrderBy(x => x, StringComparer.Ordinal),
            E.Assemblies.Select(a => a.Name).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void EveryTypeInTheSpecWasEmittedAndRead()
    {
        Assert.Equal(
            Spec.AllTypes.Select(t => t.FullName).OrderBy(x => x, StringComparer.Ordinal),
            E.Types.Select(t => t.FullName).OrderBy(x => x, StringComparer.Ordinal));
    }

    /// <summary>
    /// Reading is total: every call the spec asked for is present as an edge, and every
    /// edge corresponds to a call the spec asked for. Neither direction alone is enough.
    /// </summary>
    [Fact]
    public void MethodEdgesAreExactlyTheCallsTheSpecAskedFor()
    {
        var expected = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var t in Spec.AllTypes)
        foreach (var m in t.Methods)
        foreach (var op in m.Body.OfType<CallCorpus>())
            if ($"{t.FullName}::{m.Name}" != $"{op.TypeFullName}::{op.Method}")
                expected.Add($"{t.FullName}::{m.Name} -> {op.TypeFullName}::{op.Method}");

        var actual = new SortedSet<string>(
            E.MethodEdges.Select(e => $"{e.From} -> {e.To}"), StringComparer.Ordinal);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EveryFrameworkCallInTheSpecThatMatchesARuleIsReportedAsABlocker()
    {
        var expected = new List<string>();
        foreach (var t in Spec.AllTypes)
        foreach (var m in t.Methods)
        foreach (var op in m.Body.OfType<CallFramework>())
            if (BlockerRules.Match($"{op.Namespace}.{op.Type}", op.Member) is not null)
                expected.Add($"{t.FullName}::{m.Name}|{op.Namespace}.{op.Type}");

        var actual = E.Blockers.Select(b => $"{b.TypeFullName}::{b.MethodName}|{b.Rule.Namespace}.{b.Rule.TypeName}").ToList();
        Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal), actual.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void ReflectionSitesAreExactlyThoseTheSpecPlanted()
    {
        var expected = Spec.AllTypes.SelectMany(t => t.Methods.SelectMany(m => m.Body
            .Where(op => op is GetTypeLiteral or GetTypeComputed or ActivatorTypeOf)
            .Select(op => $"{t.FullName}::{m.Name}"))).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var actual = E.ReflectionSites.Select(s => $"{s.TypeFullName}::{s.MethodName}")
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ReadingTheSameDirectoryTwiceGivesTheSameAnswer()
    {
        var again = IlReader.Read(f.Directory);
        Assert.Equal(E.Types.Count, again.Types.Count);
        Assert.Equal(E.MethodEdges.Count, again.MethodEdges.Count);
        Assert.Equal(
            E.MethodEdges.Select(x => x.ToString()),
            again.MethodEdges.Select(x => x.ToString()));
    }

    /// <summary>
    /// Cecil memory-maps the files it reads. If the reader does not release them the
    /// analyser can be run exactly once per process, which is discovered in CI, not here.
    /// </summary>
    [Fact]
    public void ReadingDoesNotHoldTheFilesOpen()
    {
        var dir = Path.Combine(Path.GetTempPath(), "arch-lock-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            CorpusBuilder.Emit(CorpusSpec.Contoso(), dir);
            _ = IlReader.Read(dir);
            Directory.Delete(dir, true);
            Assert.False(Directory.Exists(dir));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
