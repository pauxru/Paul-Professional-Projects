namespace Archaeologist.Core;

/// <summary>An assembly-level strongly connected component: things that must move together.</summary>
public sealed record MigrationUnit(
    string Id,
    IReadOnlyList<string> Assemblies,
    IReadOnlyList<string> Types,
    IReadOnlyList<IReadOnlyList<string>> TypeKnots)
{
    public bool IsPackagingArtefact => Assemblies.Count > 1 && TypeKnots.Count == 0;

    /// <summary>Types that would have to move to a new assembly to dissolve this unit.</summary>
    public IReadOnlyList<string> KnottedTypes =>
        TypeKnots.SelectMany(k => k).OrderBy(t => t, StringComparer.Ordinal).ToList();
}

public sealed record Difficulty(
    string Assembly,
    int OwnScore,
    int UnitScore,
    int UnitSize,
    bool SourceAvailable,
    bool DependsOnUnbuildable);

public sealed record Plan(
    IReadOnlyList<MigrationUnit> Units,
    IReadOnlyDictionary<string, int> UnitWaves,
    IReadOnlyList<Difficulty> Difficulties,
    IReadOnlyList<string> CriticalPath,
    IReadOnlyList<string> UnbuildableAssemblies,
    IReadOnlyList<string> ConstrainedByUnbuildable);

public static class MigrationPlanner
{
    public static DiGraph AssemblyGraph(Estate e) =>
        new(e.Assemblies.Select(a => a.Name), e.AssemblyEdges);

    public static DiGraph TypeGraph(Estate e) =>
        new(e.Types.Select(t => t.FullName), e.TypeEdges);

    /// <summary>
    /// The central measurement. An assembly-level cycle says "these ship together"; it does
    /// not say "these are entangled". Whether they are entangled is a question about types,
    /// and the two answers differ far more often than the tooling admits.
    /// </summary>
    public static IReadOnlyList<MigrationUnit> Units(Estate e)
    {
        var asmGraph = AssemblyGraph(e);
        var typeGraph = TypeGraph(e);
        var typesByAsm = e.Types
            .GroupBy(t => t.AssemblyName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(t => t.FullName).ToList(), StringComparer.Ordinal);

        var units = new List<MigrationUnit>();
        foreach (var comp in Graphs.StronglyConnectedComponents(asmGraph))
        {
            var types = comp
                .SelectMany(a => typesByAsm.TryGetValue(a, out var ts) ? ts : [])
                .OrderBy(t => t, StringComparer.Ordinal).ToList();

            var knots = Graphs.StronglyConnectedComponents(typeGraph.InducedOn(types))
                .Where(c => c.Count > 1)
                .ToList();

            units.Add(new MigrationUnit(comp[0], comp, types, knots));
        }
        return units.OrderBy(u => u.Id, StringComparer.Ordinal).ToList();
    }

    public static DiGraph Condensation(Estate e, IReadOnlyList<MigrationUnit> units)
    {
        var unitOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var u in units)
        foreach (var a in u.Assemblies)
            unitOf[a] = u.Id;

        var edges = new SortedDictionary<string, Edge>(StringComparer.Ordinal);
        foreach (var edge in e.AssemblyEdges)
        {
            var (a, b) = (unitOf[edge.From], unitOf[edge.To]);
            if (a == b) continue;
            var key = $"{a}\u0000{b}";
            edges[key] = edges.TryGetValue(key, out var prev)
                ? prev with { Weight = prev.Weight + edge.Weight }
                : new Edge(a, b, edge.Weight);
        }
        return new DiGraph(units.Select(u => u.Id), edges.Values);
    }

    public static Plan Build(Estate e)
    {
        var units = Units(e);
        var cond = Condensation(e, units);
        var waves = Graphs.Waves(cond);

        var unitOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var u in units)
        foreach (var a in u.Assemblies)
            unitOf[a] = u.Id;

        var own = e.Assemblies.ToDictionary(a => a.Name, _ => 0, StringComparer.Ordinal);
        foreach (var b in e.Blockers) own[b.AssemblyName] += (int)b.Rule.Severity;

        var unitScore = units.ToDictionary(
            u => u.Id, u => u.Assemblies.Sum(a => own[a]), StringComparer.Ordinal);

        var unbuildable = e.Assemblies.Where(a => !a.SourceAvailable)
            .Select(a => a.Name).OrderBy(x => x, StringComparer.Ordinal).ToList();

        // Anything that reaches an assembly nobody can rebuild inherits its ceiling --
        // including the unbuildable assembly itself, which is the most constrained thing
        // in the estate. This is a blast radius, so the epicentre is inside it.
        var asmGraph = AssemblyGraph(e);
        var constrained = new SortedSet<string>(unbuildable, StringComparer.Ordinal);
        foreach (var start in e.Assemblies.Select(a => a.Name))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { start };
            var stack = new Stack<string>([start]);
            while (stack.Count > 0)
                foreach (var edge in asmGraph.Out(stack.Pop()))
                    if (seen.Add(edge.To))
                    {
                        stack.Push(edge.To);
                        if (unbuildable.Contains(edge.To)) constrained.Add(start);
                    }
        }

        var difficulties = e.Assemblies
            .Select(a => new Difficulty(a.Name, own[a.Name], unitScore[unitOf[a.Name]],
                units.First(u => u.Id == unitOf[a.Name]).Assemblies.Count,
                a.SourceAvailable, constrained.Contains(a.Name)))
            .OrderBy(d => d.Assembly, StringComparer.Ordinal).ToList();

        return new Plan(units, waves, difficulties, LongestChain(cond, waves),
            unbuildable, constrained.ToList());
    }

    /// <summary>The chain that sets the schedule. Shortening anything else changes nothing.</summary>
    private static IReadOnlyList<string> LongestChain(
        DiGraph cond, IReadOnlyDictionary<string, int> waves)
    {
        var start = cond.Nodes.OrderByDescending(n => waves[n])
            .ThenBy(n => n, StringComparer.Ordinal).First();
        var path = new List<string> { start };
        var cur = start;
        while (true)
        {
            var next = cond.Out(cur)
                .Where(edge => waves[edge.To] == waves[cur] - 1)
                .OrderBy(edge => edge.To, StringComparer.Ordinal)
                .Select(edge => edge.To).FirstOrDefault();
            if (next is null) break;
            path.Add(next);
            cur = next;
        }
        return path;
    }

    /// <summary>
    /// Kendall tau-b. Used to ask whether ranking assemblies by their own blockers gives
    /// the same running order as ranking them by what they actually drag along.
    /// </summary>
    public static double KendallTau(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        if (a.Count != b.Count) throw new ArgumentException("length mismatch");
        long concordant = 0, discordant = 0, tiesA = 0, tiesB = 0;
        for (var i = 0; i < a.Count; i++)
        for (var j = i + 1; j < a.Count; j++)
        {
            var da = Math.Sign(a[i] - a[j]);
            var db = Math.Sign(b[i] - b[j]);
            if (da == 0 && db == 0) { tiesA++; tiesB++; continue; }
            if (da == 0) { tiesA++; continue; }
            if (db == 0) { tiesB++; continue; }
            if (da == db) concordant++; else discordant++;
        }
        var n0 = (double)a.Count * (a.Count - 1) / 2;
        var denom = Math.Sqrt((n0 - tiesA) * (n0 - tiesB));
        return denom == 0 ? 0 : (concordant - discordant) / denom;
    }
}
