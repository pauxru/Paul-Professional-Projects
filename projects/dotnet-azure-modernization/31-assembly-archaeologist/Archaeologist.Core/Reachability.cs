namespace Archaeologist.Core;

/// <summary>
/// Four answers to "what is dead?", ordered from the one people want to the one that is
/// true. The distance between them is the finding.
/// </summary>
public enum ReachabilityMode
{
    /// <summary>Follow call edges only. Fast, comfortable, and wrong.</summary>
    CallsOnly,

    /// <summary>Also follow reflection whose target is a literal in the IL.</summary>
    DecidableReflection,

    /// <summary>
    /// Also honour computed reflection, constrained by whatever string prefix could be
    /// recovered from the IL. Sound provided the prefix itself is constant.
    /// </summary>
    PrefixConstrained,

    /// <summary>
    /// Also honour computed reflection with no constraint at all: one such site in
    /// reachable code and nothing in the process can be proven dead.
    /// </summary>
    FullySound,
}

public sealed record ReachabilityResult(
    ReachabilityMode Mode,
    IReadOnlyList<string> LiveMethods,
    IReadOnlyList<string> LiveTypes,
    IReadOnlyList<string> LiveAssemblies,
    IReadOnlyList<string> DeadTypes,
    IReadOnlyList<string> DeadAssemblies,
    int UnconstrainedSitesHonoured);

public static class Reachability
{
    public static ReachabilityResult From(Estate estate, string entryMethodId, ReachabilityMode mode)
    {
        var methodsByType = estate.Methods
            .GroupBy(m => m.TypeFullName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(m => m.Id).ToList(), StringComparer.Ordinal);

        var sitesByMethod = estate.ReflectionSites
            .GroupBy(r => $"{r.TypeFullName}::{r.MethodName}", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var outEdges = estate.MethodEdges
            .GroupBy(e => e.From, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.To).ToList(), StringComparer.Ordinal);

        var allMethodIds = estate.Methods.Select(m => m.Id).ToList();
        var live = new SortedSet<string>(StringComparer.Ordinal);
        var work = new Stack<string>();
        var unconstrained = 0;

        void Enter(string id)
        {
            if (live.Add(id)) work.Push(id);
        }

        void EnterType(string typeFullName)
        {
            // Reflectively obtaining a type is not the same as calling one of its methods,
            // but nothing downstream is visible, so every method on it must be assumed
            // reachable. Narrowing this would need the call site, which is the thing
            // reflection removed.
            if (methodsByType.TryGetValue(typeFullName, out var ms))
                foreach (var m in ms) Enter(m);
        }

        Enter(entryMethodId);

        while (work.Count > 0)
        {
            var m = work.Pop();
            if (outEdges.TryGetValue(m, out var callees))
                foreach (var c in callees) Enter(c);

            if (mode == ReachabilityMode.CallsOnly) continue;
            if (!sitesByMethod.TryGetValue(m, out var sites)) continue;

            foreach (var site in sites)
            {
                if (site.IsDecidable)
                {
                    if (site.ResolvedTarget is not null) EnterType(site.ResolvedTarget);
                    continue;
                }

                if (mode == ReachabilityMode.DecidableReflection) continue;

                if (mode == ReachabilityMode.PrefixConstrained && site.RecoveredPrefix is { } prefix)
                {
                    foreach (var t in estate.Types)
                        if (t.FullName.StartsWith(prefix, StringComparison.Ordinal))
                            EnterType(t.FullName);
                    continue;
                }

                // Nothing bounds this site. The only sound answer is "everything".
                unconstrained++;
                foreach (var id in allMethodIds) Enter(id);
            }
        }

        var liveTypes = new SortedSet<string>(
            live.Select(estate.TypeOfMethod), StringComparer.Ordinal);
        var liveAsms = new SortedSet<string>(
            liveTypes.Select(estate.AssemblyOfType), StringComparer.Ordinal);

        var deadTypes = estate.Types.Select(t => t.FullName)
            .Where(t => !liveTypes.Contains(t))
            .OrderBy(t => t, StringComparer.Ordinal).ToList();
        var deadAsms = estate.Assemblies.Select(a => a.Name)
            .Where(a => !liveAsms.Contains(a))
            .OrderBy(a => a, StringComparer.Ordinal).ToList();

        return new ReachabilityResult(mode, live.ToList(), liveTypes.ToList(), liveAsms.ToList(),
            deadTypes, deadAsms, unconstrained);
    }
}
