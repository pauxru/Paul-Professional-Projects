namespace Archaeologist.Core;

public sealed record Dissolution(
    string UnitId,
    bool Possible,
    IReadOnlyList<string> TypesToExtract,
    bool QuarantinesAKnot,
    string Reason);

/// <summary>
/// "Your assemblies have a cycle" is a fact. "Move these two types into a new assembly and
/// the cycle is gone, without touching a line of code" is a decision. This turns the first
/// into the second, exactly, by searching every subset of the unit's types smallest-first.
///
/// The subtlety worth the search: the extracted assembly is allowed to be internally
/// cyclic, because a cycle inside one assembly is not a build-order problem. So even a
/// genuinely entangled unit can be linearised -- by quarantining the tangle into a single
/// new assembly rather than by pretending it is not there.
/// </summary>
public static class CycleDissolver
{
    public const int MaxTypesForExactSearch = 22;
    private const string Extracted = "\u0000extracted";

    public static Dissolution Dissolve(Estate estate, MigrationUnit unit)
    {
        if (unit.Assemblies.Count == 1)
            return new Dissolution(unit.Id, true, [], false, "not a cycle");

        var types = unit.Types;
        if (types.Count > MaxTypesForExactSearch)
            return new Dissolution(unit.Id, false, [], false,
                $"{types.Count} types exceeds the exact search limit of {MaxTypesForExactSearch}");

        var index = types.Select((t, i) => (t, i))
            .ToDictionary(x => x.t, x => x.i, StringComparer.Ordinal);
        var inUnit = types.ToHashSet(StringComparer.Ordinal);
        var home = types.ToDictionary(t => t, estate.AssemblyOfType, StringComparer.Ordinal);
        var knotted = unit.KnottedTypes.ToHashSet(StringComparer.Ordinal);

        // Only edges inside the unit can close a cycle inside the unit: a strongly
        // connected component has, by definition, no path back in from outside it.
        var internalEdges = estate.TypeEdges
            .Where(e => inUnit.Contains(e.From) && inUnit.Contains(e.To))
            .ToList();

        for (var k = 1; k <= types.Count; k++)
        foreach (var subset in Combinations(types.Count, k))
        {
            var moved = new bool[types.Count];
            foreach (var i in subset) moved[i] = true;

            string Owner(string t) => moved[index[t]] ? Extracted : home[t];

            var edges = new HashSet<(string, string)>();
            foreach (var e in internalEdges)
            {
                var (a, b) = (Owner(e.From), Owner(e.To));
                if (a != b) edges.Add((a, b));
            }

            var g = new DiGraph(unit.Assemblies.Append(Extracted),
                edges.Select(p => new Edge(p.Item1, p.Item2, 1)));
            if (Graphs.StronglyConnectedComponents(g).Any(c => c.Count > 1)) continue;

            var picked = subset.Select(i => types[i])
                .OrderBy(t => t, StringComparer.Ordinal).ToList();
            var quarantine = picked.Any(knotted.Contains);
            return new Dissolution(unit.Id, true, picked, quarantine,
                quarantine
                    ? $"moving {k} type(s) into one new assembly linearises the build; " +
                      "the tangle survives inside that assembly and still needs a code change"
                    : $"moving {k} type(s) into a new assembly removes the cycle; no code changes");
        }

        return new Dissolution(unit.Id, false, [], false, "no extraction dissolves this unit");
    }

    /// <summary>Subsets of size k, in a fixed order so the reported answer never wobbles.</summary>
    private static IEnumerable<int[]> Combinations(int n, int k)
    {
        if (k > n) yield break;
        var c = Enumerable.Range(0, k).ToArray();
        while (true)
        {
            yield return (int[])c.Clone();
            var i = k - 1;
            while (i >= 0 && c[i] == n - k + i) i--;
            if (i < 0) yield break;
            c[i]++;
            for (var j = i + 1; j < k; j++) c[j] = c[j - 1] + 1;
        }
    }
}
