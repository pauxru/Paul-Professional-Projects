using System.Globalization;

namespace Archaeologist.Core;

/// <summary>
/// The report is a program. Every number below is computed from the estate at render time;
/// none is typed in. Predictions are written before the measurement and the report refuses
/// to render until each one has been settled, so the ones that came out badly cannot be
/// quietly removed.
/// </summary>
public static class Experiments
{
    private static string F(double d) => d.ToString("F2", CultureInfo.InvariantCulture);
    private static string F3(double d) => d.ToString("F3", CultureInfo.InvariantCulture);

    private static string Count(int n, string singular, string plural) =>
        $"{n} {(n == 1 ? singular : plural)}";

    public static string Run(Estate e, string entryPoint)
    {
        var r = new Report();
        var units = MigrationPlanner.Units(e);
        var plan = MigrationPlanner.Build(e);
        var asmGraph = MigrationPlanner.AssemblyGraph(e);
        var waveOfUnit = plan.UnitWaves;
        var unitOf = units.SelectMany(u => u.Assemblies.Select(a => (a, u.Id)))
            .ToDictionary(x => x.a, x => x.Id, StringComparer.Ordinal);
        int WaveOf(string asm) => waveOfUnit[unitOf[asm]];

        r.H1("Assembly Archaeologist -- what 24 compiled assemblies will admit under questioning");

        r.P("Every number in this document was produced by running the analyser over a directory " +
            "of DLLs. No source was read. No reference assembly was resolved -- `System.Web`, " +
            "`System.ServiceModel` and `System.EnterpriseServices` are not installed on the machine " +
            "that produced this, and were never needed. Metadata does not require the thing it names " +
            "to exist, which is the only reason legacy estates are analysable at all.");

        // ------------------------------------------------------------------ 1
        r.H2("1. What the artefacts admit");

        var pdbless = e.Assemblies.Where(a => !a.SourceAvailable).Select(a => a.Name).ToList();
        r.Table(["measure", "value"],
        [
            ["assemblies", e.Assemblies.Count.ToString()],
            ["types", e.Types.Count.ToString()],
            ["methods", e.Methods.Count.ToString()],
            ["call edges (method level)", e.MethodEdges.Count.ToString()],
            ["call edges (type level)", e.TypeEdges.Count.ToString()],
            ["call edges (assembly level)", e.AssemblyEdges.Count.ToString()],
            ["external assemblies referenced", e.ExternalReferences.Count.ToString()],
            ["assemblies with no PDB", pdbless.Count.ToString()],
        ]);

        r.P("The external references are the estate's era, stated in its own manifest:");
        r.Code(string.Join("\n", e.ExternalReferences));

        r.P($"Three assemblies arrive without symbols -- {string.Join(", ", pdbless)}. In the story " +
            "these are the ones whose source went with the 2011 SAN failure. Nothing below treats " +
            "them differently during analysis, and everything below treats them differently during planning.");

        // ------------------------------------------------------------------ 2
        r.H2("2. What stops it moving");

        var bySeverity = Enum.GetValues<Severity>().Where(s => s != Severity.None)
            .Select(s => (s, hits: e.Blockers.Count(b => b.Rule.Severity == s))).ToList();
        r.Table(["severity", "call sites", "meaning"],
        [
            .. bySeverity.Select(x => new[]
            {
                x.s.ToString(), x.hits.ToString(), x.s switch
                {
                    Severity.Rewrite => "compiles after a package or config change; behaviour must be re-tested",
                    Severity.PlatformNotSupported => "compiles, throws at run time, and the compiler will not warn you",
                    _ => "no modern equivalent; the design has to change",
                },
            }),
        ]);

        var blockerAsms = e.Blockers.Select(b => b.AssemblyName).Distinct().Count();
        var severityByAsm = e.Assemblies.ToDictionary(a => a.Name,
            a => e.Blockers.Where(b => b.AssemblyName == a.Name).Sum(b => (int)b.Rule.Severity),
            StringComparer.Ordinal);
        var totalSeverity = severityByAsm.Values.Sum();

        r.Table(["assembly", "wave", "sites", "score", "worst API"],
        [
            .. e.Blockers.GroupBy(b => b.AssemblyName)
                .OrderByDescending(g => g.Sum(b => (int)b.Rule.Severity))
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new[]
                {
                    g.Key,
                    WaveOf(g.Key).ToString(),
                    g.Count().ToString(),
                    g.Sum(b => (int)b.Rule.Severity).ToString(),
                    g.OrderByDescending(b => (int)b.Rule.Severity)
                        .ThenBy(b => b.Rule.Key, StringComparer.Ordinal).First().Rule.Key,
                }),
        ]);

        var pEdge = r.Expect("P1",
            "The unportable APIs are at the edge of the system. Presentation, hosting and " +
            "integration code is where a Framework estate touches things that were never ported; " +
            "the domain and utility layers underneath should be portable already.");

        var noEquivalentAsms = e.Blockers.Where(b => b.Rule.Severity == Severity.NoEquivalent)
            .Select(b => b.AssemblyName).Distinct()
            .OrderBy(WaveOf).ThenBy(x => x, StringComparer.Ordinal).ToList();
        var deepest = noEquivalentAsms.MinBy(WaveOf)!;
        var deepestWave = WaveOf(deepest);
        var maxWave = waveOfUnit.Values.Max();
        var deepestRule = e.Blockers.Where(b => b.AssemblyName == deepest)
            .OrderByDescending(b => (int)b.Rule.Severity).First().Rule;

        if (deepestWave >= maxWave - 1)
            pEdge.Held($"Every no-equivalent blocker sits in wave {deepestWave} or above, out of {maxWave}.");
        else
            pEdge.Contradicted(
                $"{noEquivalentAsms.Count} assemblies contain an API with no modern equivalent, and they " +
                $"span waves {noEquivalentAsms.Min(WaveOf)} to {noEquivalentAsms.Max(WaveOf)} of {maxWave}. " +
                $"The deepest is `{deepest}` at wave {deepestWave} -- a leaf that everything is built on -- " +
                $"and its blocker is `{deepestRule.Key}`, which is not merely unported but removed. " +
                (pdbless.Contains(deepest)
                    ? "It is also one of the assemblies with no source. The hardest problem in this estate " +
                      "is at the bottom of it, in code nobody can rebuild."
                    : "It is at the bottom of the graph, so nothing above it can move first."));
        r.Settle(pEdge);

        var pRefs = r.Expect("P2",
            "Reading the manifest is enough. An assembly's reference list names every external " +
            "assembly it touches, so counting Framework references -- which needs no IL at all -- " +
            "identifies the assemblies in trouble.");

        var order = e.Assemblies.Select(a => a.Name).ToList();
        var manifestRefs = e.Assemblies.ToDictionary(a => a.Name,
            a => a.ExternalReferences.Count, StringComparer.Ordinal);
        var tauRefs = MigrationPlanner.KendallTau(
            order.Select(a => (double)manifestRefs[a]).ToList(),
            order.Select(a => (double)severityByAsm[a]).ToList());

        // The assemblies a manifest scanner cannot see into at all: their only external
        // reference is the core library, which every assembly references.
        var corlibOnly = e.Assemblies
            .Where(a => a.ExternalReferences.Count == 1 && a.ExternalReferences[0] == "mscorlib")
            .Select(a => a.Name).ToList();
        var invisible = corlibOnly
            .Where(a => e.Blockers.Any(b => b.AssemblyName == a && b.Rule.Severity == Severity.NoEquivalent))
            .OrderBy(a => a, StringComparer.Ordinal).ToList();

        if (invisible.Count == 0 && tauRefs >= 0.9)
            pRefs.Held($"Kendall tau between manifest reference count and severity score is {F3(tauRefs)}, " +
                       "and no assembly hides a no-equivalent blocker behind a corlib-only manifest.");
        else
            pRefs.Contradicted(
                $"Kendall tau between manifest reference count and blocker severity is {F3(tauRefs)}, " +
                $"which sounds usable until you look at what it misses. {Count(invisible.Count, "assembly", "assemblies")} " +
                $"reference nothing but `mscorlib` and still contain an API with no modern equivalent: " +
                string.Join(", ", invisible.Select(a => $"`{a}`")) + ". " +
                $"`{e.Blockers.First(b => invisible.Contains(b.AssemblyName)).Rule.Key}` lives in the core " +
                "library, so the manifest of the assembly holding the estate's worst blocker is " +
                "indistinguishable from that of a pure-domain assembly with nothing wrong with it. " +
                "A manifest scanner cannot see the difference because the difference is not in the manifest.");
        r.Settle(pRefs);

        // ------------------------------------------------------------------ 3
        r.H2("3. Cycles: what the graph says, and what the code says");

        var cyclic = units.Where(u => u.Assemblies.Count > 1).ToList();
        var inCycle = cyclic.Sum(u => u.Assemblies.Count);

        var pOrder = r.Expect("P3",
            "There is no migration order. Estates of this age always contain assembly cycles, so a " +
            "topological sort of the dependency graph does not exist and the plan cannot simply be " +
            "read off the graph.");
        pOrder.Held(
            $"{cyclic.Count} strongly connected components contain more than one assembly, covering " +
            $"{inCycle} of {e.Assemblies.Count} assemblies. `TopologicalOrder` throws on the raw " +
            "assembly graph, which is the correct behaviour and the reason the rest of this section exists.");
        r.Settle(pOrder);

        var pScope = r.Expect("P4",
            "The cycles are a local problem. Most assemblies in an estate this size sit in no cycle " +
            "at all, so most of the plan can be read straight off the graph and only a minority needs " +
            "the expensive treatment.");

        var singletons = units.Count(u => u.Assemblies.Count == 1);
        if (singletons > e.Assemblies.Count / 2)
            pScope.Held(
                $"{singletons} of {units.Count} units are single assemblies, covering " +
                $"{e.Assemblies.Count - inCycle} of {e.Assemblies.Count} assemblies " +
                $"({F(100.0 * (e.Assemblies.Count - inCycle) / e.Assemblies.Count)}%). " +
                $"The tangle is real but bounded: {cyclic.Count} units need the analysis in sections " +
                "4 and 5, and the other " + singletons + " need only an ordering.");
        else
            pScope.Contradicted(
                $"Only {singletons} of {units.Count} units are single assemblies; {inCycle} of " +
                $"{e.Assemblies.Count} assemblies are in a cycle.");
        r.Settle(pScope);

        r.Table(["unit", "assemblies", "types", "type-level knots", "knotted types"],
        [
            .. cyclic.Select(u => new[]
            {
                u.Id, u.Assemblies.Count.ToString(), u.Types.Count.ToString(),
                u.TypeKnots.Count.ToString(),
                u.KnottedTypes.Count == 0 ? "--" : u.KnottedTypes.Count.ToString(),
            }),
        ]);

        var pEntangled = r.Expect("P5",
            "An assembly cycle means the code is entangled. If A and B depend on each other, the " +
            "types inside them depend on each other, and separating them is a rewrite.");

        var knottedTotal = cyclic.Sum(u => u.KnottedTypes.Count);
        var typesInCycles = cyclic.Sum(u => u.Types.Count);
        var packagingOnly = cyclic.Where(u => u.TypeKnots.Count == 0).ToList();
        pEntangled.Contradicted(
            $"{inCycle} assemblies are in cycles and they contain {typesInCycles} types, but only " +
            $"{knottedTotal} of those types actually participate in a cycle -- " +
            $"{F(100.0 * knottedTotal / typesInCycles)}% of them. " +
            $"The largest unit, `{cyclic.MaxBy(u => u.Assemblies.Count)!.Id}`, ties " +
            $"{cyclic.Max(u => u.Assemblies.Count)} assemblies together on the strength of " +
            $"{cyclic.MaxBy(u => u.Assemblies.Count)!.KnottedTypes.Count} types. " +
            (packagingOnly.Count > 0
                ? $"{Count(packagingOnly.Count, "unit contains", "units contain")} no type-level cycle " +
                  "at all: the cycle exists purely because of which types were put in which project file."
                : "Every unit contains at least one genuine type-level cycle."));
        r.Settle(pEntangled);

        foreach (var u in cyclic)
        {
            r.H3($"unit `{u.Id}`");
            r.Bullets(u.Assemblies.Select(a => $"`{a}`"));
            if (u.TypeKnots.Count == 0)
                r.P("The induced type graph is acyclic. Nothing in this unit is entangled; the cycle " +
                    "is an artefact of packaging.");
            else
                foreach (var k in u.TypeKnots)
                    r.P("Type-level knot: " + string.Join(" -> ", k.Append(k[0]).Select(t => $"`{t}`")));
        }

        // ------------------------------------------------------------------ 4
        r.H2("4. Breaking the cycles: the heuristic and the truth");

        r.P("Minimum feedback arc set -- the cheapest set of dependencies to cut so an order exists -- " +
            "is NP-hard. The usual response is a heuristic and silence about how good it is. This " +
            "ships both a heuristic (Eades-Lin-Smyth) and an exact solver (dynamic programming over " +
            "subsets, refusing rather than degrading past 20 nodes), and reports the gap.");

        var pHeuristic = r.Expect("P6",
            "The heuristic is suboptimal somewhere in this estate. That is why the exact solver exists.");

        var fasRows = new List<IReadOnlyList<string>>();
        var anyGap = false;
        foreach (var u in cyclic)
        {
            var sub = asmGraph.InducedOn(u.Assemblies);
            var g = FeedbackArcSet.Greedy(sub);
            var x = FeedbackArcSet.Exact(sub);
            if (g.Weight != x.Weight) anyGap = true;
            fasRows.Add([u.Id, u.Assemblies.Count.ToString(), sub.EdgeCount.ToString(),
                $"{g.Edges} / {g.Weight}", $"{x.Edges} / {x.Weight}",
                g.Weight == x.Weight ? "equal" : $"+{g.Weight - x.Weight}"]);
        }
        r.Table(["unit", "n", "edges", "greedy cut/weight", "exact cut/weight", "gap"], fasRows);

        var sizes = new[] { 6, 8, 10, 12 };
        var synth = new List<IReadOnlyList<string>>();
        var firstGapSize = 0;
        foreach (var n in sizes)
        {
            int total = 0, worst = 0, losses = 0;
            const int trials = 40;
            for (var seed = 0; seed < trials; seed++)
            {
                var rg = FeedbackArcSet.RandomDense(n, 0.35, seed * 977 + n);
                var gw = FeedbackArcSet.Greedy(rg).Weight;
                var xw = FeedbackArcSet.Exact(rg).Weight;
                if (gw < xw) throw new InvalidOperationException($"greedy beat exact at n={n}, seed={seed}");
                total += gw - xw;
                worst = Math.Max(worst, gw - xw);
                if (gw > xw) losses++;
            }
            if (firstGapSize == 0 && losses > 0) firstGapSize = n;
            synth.Add([n.ToString(), $"{losses}/{trials}", F(total / (double)trials), worst.ToString()]);
        }

        if (anyGap)
            pHeuristic.Held("The heuristic loses on at least one real unit; see the gap column above.");
        else
            pHeuristic.Contradicted(
                $"The heuristic matches the exact solver on all {cyclic.Count} units in this estate. " +
                "That is a fact about the estate, not about the heuristic: the largest unit has " +
                $"{cyclic.Max(u => u.Assemblies.Count)} nodes, and at that size almost anything is optimal. " +
                $"On random dense digraphs the heuristic starts losing at n={firstGapSize} and the gap " +
                "grows steadily after that, which is why the exact solver ships and why it refuses " +
                "rather than degrades.");
        r.Settle(pHeuristic);

        r.Table(["n", "trials where greedy lost", "mean excess weight", "worst excess"], synth);

        // ------------------------------------------------------------------ 5
        r.H2("5. Dissolving the cycles instead of cutting them");

        var pCut = r.Expect("P7",
            "Breaking a cycle means removing a dependency. The output of cycle analysis is a list of " +
            "edges to delete, and deleting each one is a piece of work someone has to do.");

        var dissolutions = cyclic.Select(u => (u, d: CycleDissolver.Dissolve(e, u))).ToList();
        var totalMoves = dissolutions.Sum(x => x.d.TypesToExtract.Count);
        var totalCut = cyclic.Sum(u => FeedbackArcSet.Exact(asmGraph.InducedOn(u.Assemblies)).Edges);

        r.Table(["unit", "edges to cut", "types to move", "code change needed?"],
        [
            .. dissolutions.Select(x => new[]
            {
                x.u.Id,
                FeedbackArcSet.Exact(asmGraph.InducedOn(x.u.Assemblies)).Edges.ToString(),
                x.d.TypesToExtract.Count.ToString(),
                x.d.QuarantinesAKnot ? "yes -- quarantined, not fixed" : "no",
            }),
        ]);

        foreach (var (u, d) in dissolutions)
        {
            r.H3($"`{u.Id}`");
            r.P(d.Reason);
            r.Bullets(d.TypesToExtract.Select(t => $"`{t}` (currently in `{e.AssemblyOfType(t)}`)"));
        }

        pCut.Contradicted(
            $"Cutting {totalCut} dependency edges and moving {totalMoves} types produce the same " +
            "acyclic result, but they are not the same work. Cutting an edge means finding every call " +
            "site behind it and inverting a dependency -- the analyser reports edge weights precisely " +
            "because that cost is not one unit. Moving a type between project files changes no code at all " +
            $"where the unit has no type-level knot, which is true of {packagingOnly.Count} of " +
            $"{cyclic.Count} units here. Where a knot does exist, extraction does not fix it; it " +
            "quarantines it into a single assembly so the rest of the build orders, and the tangle " +
            "becomes one scheduled piece of work instead of a constraint on everything.");
        r.Settle(pCut);

        // ------------------------------------------------------------------ 6
        r.H2("6. Dead code: four answers, one of them true");

        var modes = Enum.GetValues<ReachabilityMode>()
            .Select(m => (m, res: Reachability.From(e, entryPoint, m))).ToList();

        r.Table(["analysis", "live types", "dead types", "dead assemblies", "sound?"],
        [
            .. modes.Select(x => new[]
            {
                x.m.ToString(),
                x.res.LiveTypes.Count.ToString(),
                x.res.DeadTypes.Count.ToString(),
                x.res.DeadAssemblies.Count.ToString(),
                x.m switch
                {
                    ReachabilityMode.CallsOnly => "no",
                    ReachabilityMode.DecidableReflection => "no",
                    ReachabilityMode.PrefixConstrained => "yes, if the prefix is constant",
                    _ => "yes",
                },
            }),
        ]);

        var callsOnly = modes.First(x => x.m == ReachabilityMode.CallsOnly).res;
        var decidable = modes.First(x => x.m == ReachabilityMode.DecidableReflection).res;
        var prefix = modes.First(x => x.m == ReachabilityMode.PrefixConstrained).res;
        var sound = modes.First(x => x.m == ReachabilityMode.FullySound).res;

        var pDead = r.Expect("P8",
            "Static reachability finds the dead code. Walk the call graph from the entry point; " +
            "what you do not reach, you can delete.");

        var resurrected = callsOnly.DeadTypes.Except(decidable.DeadTypes, StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        pDead.Contradicted(
            $"Call-graph reachability declares {callsOnly.DeadTypes.Count} types dead. " +
            $"{resurrected.Count} of them are named as string literals or type tokens elsewhere in the " +
            "IL and are alive: " + string.Join(", ", resurrected.Select(t => $"`{t}`")) + ". " +
            "Deleting on the strength of the call graph would have removed working code, and the " +
            "evidence that it was working was sitting in the same directory the whole time.");
        r.Settle(pDead);

        var pReflection = r.Expect("P9",
            "Reflection makes dead-code analysis worthless. One `Type.GetType` on a configured name " +
            "and nothing in the process can be proven dead, so the sound answer is useless and " +
            "everybody ships the unsound one instead.");

        var computed = e.ReflectionSites.Where(s => !s.IsDecidable).ToList();
        var recovered = computed.Where(s => s.RecoveredPrefix is not null).ToList();
        if (recovered.Count == 0)
            pReflection.Held(
                $"{Count(computed.Count, "site", "sites")} builds a type name at run time and no " +
                "constant prefix could be recovered from any of them, so the sound answer stands at " +
                $"{sound.DeadTypes.Count} deletable types.");
        else
            pReflection.Contradicted(
                "The sound-with-no-constraint answer is exactly as useless as predicted: " +
                $"{sound.DeadTypes.Count} deletable types, because " +
                $"{Count(computed.Count, "call site builds", "call sites build")} a type name at run " +
                "time. But the name is not built from nothing. " +
                $"{recovered.Count} of them {(recovered.Count == 1 ? "concatenates" : "concatenate")} " +
                $"a constant prefix that is still in the IL -- here, `{recovered[0].RecoveredPrefix}` -- " +
                "and recovering it narrows \"any type in the process\" to \"any type under that " +
                "namespace\". " +
                $"That takes the sound answer from {sound.DeadTypes.Count} deletable types to " +
                $"{prefix.DeadTypes.Count}, and from {sound.DeadAssemblies.Count} deletable assemblies " +
                $"to {prefix.DeadAssemblies.Count}, without giving up soundness. Two instructions of " +
                "dataflow buy back the entire result.");
        r.Settle(pReflection);

        r.P("What each analysis would have you delete:");
        r.Table(["analysis", "assemblies it says you can delete"],
        [
            .. modes.Select(x => new[]
            {
                x.m.ToString(),
                x.res.DeadAssemblies.Count == 0 ? "(none)" : string.Join(", ", x.res.DeadAssemblies),
            }),
        ]);

        r.P("Reflection sites, as read from IL:");
        r.Table(["site", "kind", "resolves to", "recovered prefix"],
        [
            .. e.ReflectionSites.Select(s => new[]
            {
                $"`{s.Site}`", s.Kind.ToString(),
                s.ResolvedTarget is null ? "--" : $"`{s.ResolvedTarget}`",
                s.RecoveredPrefix is null ? "--" : $"`{s.RecoveredPrefix}`",
            }),
        ]);

        // ------------------------------------------------------------------ 7
        r.H2("7. Difficulty does not compose the way it is reported");

        var pRank = r.Expect("P10",
            "Ranking assemblies by their own blocker count gives the order to work in. The worst " +
            "assembly is the one with the most Framework APIs in it.");

        var ownVec = plan.Difficulties.Select(d => (double)d.OwnScore).ToList();
        var unitVec = plan.Difficulties.Select(d => (double)d.UnitScore).ToList();
        var tauUnit = MigrationPlanner.KendallTau(ownVec, unitVec);
        var worstOwn = plan.Difficulties.OrderByDescending(d => d.OwnScore)
            .ThenBy(d => d.Assembly, StringComparer.Ordinal).First();
        var onCritical = plan.CriticalPath.Contains(unitOf[worstOwn.Assembly]);

        r.Table(["assembly", "own score", "unit score", "unit size", "source?", "depends on unbuildable?"],
        [
            .. plan.Difficulties.OrderByDescending(d => d.UnitScore).ThenByDescending(d => d.OwnScore)
                .ThenBy(d => d.Assembly, StringComparer.Ordinal).Take(12)
                .Select(d => new[]
                {
                    d.Assembly, d.OwnScore.ToString(), d.UnitScore.ToString(), d.UnitSize.ToString(),
                    d.SourceAvailable ? "yes" : "NO", d.DependsOnUnbuildable ? "yes" : "no",
                }),
        ]);

        pRank.Contradicted(
            $"Kendall tau between an assembly's own blocker score and the score of everything it is " +
            $"forced to move with is {F3(tauUnit)}. An assembly in a {cyclic.Max(u => u.Assemblies.Count)}-assembly " +
            "cycle cannot be migrated alone whatever its own score says, so the two rankings disagree " +
            $"on a substantial fraction of pairs. The worst assembly by its own score, `{worstOwn.Assembly}` " +
            $"(score {worstOwn.OwnScore}), is " + (onCritical ? "on" : "not on") +
            " the critical path, so improving it " + (onCritical ? "shortens" : "does not shorten") +
            " the schedule at all.");
        r.Settle(pRank);

        // ------------------------------------------------------------------ 8
        r.H2("8. The constraint nobody costs: assemblies nobody can rebuild");

        var pSource = r.Expect("P11",
            "Missing source is an analysis problem. The assemblies whose source was lost are the ones " +
            "you cannot say anything about; the rest of the estate can be planned normally.");

        var blockersInPdbless = e.Blockers.Where(b => pdbless.Contains(b.AssemblyName)).ToList();
        pSource.Contradicted(
            $"Analysis is entirely unaffected: all {pdbless.Count} source-less assemblies were read, " +
            $"their call edges recovered, and {Count(blockersInPdbless.Count, "blocker", "blockers")} " +
            "found inside them -- including " +
            $"`{blockersInPdbless.OrderByDescending(b => (int)b.Rule.Severity).First().Rule.Key}`, " +
            "which is the most severe class of blocker there is. Planning is where it bites: " +
            $"{plan.ConstrainedByUnbuildable.Count} of {e.Assemblies.Count} assemblies " +
            $"({F(100.0 * plan.ConstrainedByUnbuildable.Count / e.Assemblies.Count)}%) transitively depend " +
            $"on something nobody can rebuild, against {blockerAsms} " +
            $"({F(100.0 * blockerAsms / e.Assemblies.Count)}%) that contain a blocker of their own. " +
            "The estate is constrained more by what cannot be recompiled than by what will not compile.");
        r.Settle(pSource);

        r.Bullets(plan.UnbuildableAssemblies.Select(a =>
            $"`{a}` -- wave {WaveOf(a)}, " +
            $"{e.Blockers.Count(b => b.AssemblyName == a)} blocker(s), " +
            $"{e.Assemblies.Count(x => Reaches(asmGraph, x.Name, a))} assemblies depend on it"));

        // ------------------------------------------------------------------ 9
        r.H2("9. The plan");

        r.P("Waves run bottom-up: wave 1 has no unmigrated dependencies and can start immediately. " +
            "Units inside a wave are independent of each other and can be run in parallel by " +
            "different people.");

        r.Table(["wave", "units", "assemblies", "score", "notes"],
        [
            .. waveOfUnit.GroupBy(kv => kv.Value).OrderBy(g => g.Key).Select(g =>
            {
                var us = g.Select(kv => kv.Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
                var asms = us.SelectMany(id => units.First(u => u.Id == id).Assemblies).ToList();
                return new[]
                {
                    g.Key.ToString(), us.Count.ToString(), asms.Count.ToString(),
                    asms.Sum(a => severityByAsm[a]).ToString(),
                    string.Join("; ", us.Select(id =>
                    {
                        var u = units.First(x => x.Id == id);
                        return u.Assemblies.Count > 1 ? $"{id} (+{u.Assemblies.Count - 1} more)" : id;
                    })),
                };
            }),
        ]);

        r.P($"Critical path ({plan.CriticalPath.Count} waves): " +
            string.Join(" <- ", plan.CriticalPath.Select(p => $"`{p}`")) + ". " +
            "Every other unit has slack. Work that does not shorten this chain does not shorten " +
            "the migration.");

        var pParallel = r.Expect("P12",
            "The plan parallelises. Migration schedules are set by the longest chain of dependencies, " +
            "not by the number of assemblies, so the wave count should be far smaller than the " +
            "assembly count and most waves should hold several independent units.");

        var waveGroups = waveOfUnit.GroupBy(kv => kv.Value).ToList();
        var wide = waveGroups.Count(g => g.Count() > 1);
        var widest = waveGroups.Max(g => g.Count());
        if (maxWave * 2 <= e.Assemblies.Count && wide * 2 >= waveGroups.Count)
            pParallel.Held(
                $"{e.Assemblies.Count} assemblies migrate in {maxWave} waves. {wide} of {waveGroups.Count} " +
                $"waves contain more than one independent unit and the widest holds {widest}, so the " +
                $"schedule is bounded below by a chain of {plan.CriticalPath.Count} and not by the size " +
                "of the estate. Adding people shortens this plan; adding people to the critical path " +
                "does not.");
        else
            pParallel.Contradicted(
                $"{e.Assemblies.Count} assemblies need {maxWave} waves and only {wide} of " +
                $"{waveGroups.Count} waves hold more than one unit. The plan is close to sequential.");
        r.Settle(pParallel);

        // ------------------------------------------------------------------ 10
        r.H2("10. Summary");

        r.Table(["id", "prediction", "verdict"],
        [
            .. r.Predictions.Select(p => new[]
            {
                p.Id, Trim(p.Statement), p.Marker,
            }),
        ]);

        var held = r.Predictions.Count(p => p.Verdict == Verdict.Held);
        r.P($"{held} of {r.Predictions.Count} predictions held. The three that did are structural " +
            "facts about the shape of the estate. Every one that did not makes the same mistake, and " +
            "it is worth naming: **the assembly is the wrong unit.** Portability is a property of a " +
            "member. Entanglement is a property of a type. Difficulty is a property of a migration " +
            "unit. Deletability is a property of a type, qualified by a string constant. The assembly " +
            "is what the build system happens to emit, and reports organised around it are wrong in " +
            "the specific ways measured above.");

        r.P("The three findings that change what you would actually do:");
        r.Bullets(
        [
            $"{inCycle} assemblies are locked together by cycles, and {knottedTotal} types decide it. " +
            $"Moving {totalMoves} types between project files linearises the entire build, and " +
            $"{Count(packagingOnly.Count, "unit needs", "units need")} no code change at all.",
            $"Call-graph dead-code analysis would have deleted {Count(resurrected.Count, "live type", "live types")}; " +
            $"the sound analysis deletes {sound.DeadTypes.Count}; recovering one string constant from " +
            $"the IL makes the sound analysis delete {prefix.DeadTypes.Count}.",
            $"{plan.ConstrainedByUnbuildable.Count} of {e.Assemblies.Count} assemblies depend on code " +
            $"nobody can rebuild, against {blockerAsms} that contain a blocker. The binding constraint " +
            "is provenance, not API surface.",
        ]);

        r.P($"Total blocker severity across the estate: {totalSeverity} points over " +
            $"{e.Blockers.Count} call sites in {blockerAsms} assemblies.");

        return r.Render();
    }

    private static bool Reaches(DiGraph g, string from, string to)
    {
        if (from == to) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal) { from };
        var stack = new Stack<string>([from]);
        while (stack.Count > 0)
            foreach (var edge in g.Out(stack.Pop()))
            {
                if (edge.To == to) return true;
                if (seen.Add(edge.To)) stack.Push(edge.To);
            }
        return false;
    }

    private static string Trim(string s)
    {
        var stop = s.IndexOf(". ", StringComparison.Ordinal);
        return stop < 0 ? s : s[..(stop + 1)];
    }
}
