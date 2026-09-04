namespace JobScheduler.Domain;

/// <summary>
/// Directed acyclic graph over job-definition identifiers used to model dependency
/// chains ("job B runs after A succeeds"), fan-in and fan-out. Pure and side-effect free.
/// </summary>
public static class DagValidator
{
    /// <summary>
    /// Kahn topological sort. <paramref name="edges"/> maps a node to the set of nodes it
    /// depends on (its predecessors). Returns an execution order where every dependency
    /// precedes its dependents. Throws <see cref="DependencyCycleException"/> on a cycle.
    /// </summary>
    public static IReadOnlyList<string> TopologicalOrder(IReadOnlyDictionary<string, IReadOnlyCollection<string>> edges)
    {
        ArgumentNullException.ThrowIfNull(edges);

        var nodes = new HashSet<string>();
        foreach (var (node, deps) in edges)
        {
            nodes.Add(node);
            foreach (var d in deps)
            {
                nodes.Add(d);
            }
        }

        // successors[x] = nodes that depend on x; indegree[x] = number of unmet dependencies.
        var successors = nodes.ToDictionary(n => n, _ => new List<string>());
        var indegree = nodes.ToDictionary(n => n, _ => 0);

        foreach (var (node, deps) in edges)
        {
            foreach (var dep in deps)
            {
                successors[dep].Add(node);
                indegree[node]++;
            }
        }

        // Deterministic ordering: process ready nodes alphabetically so results are reproducible.
        var ready = new SortedSet<string>(nodes.Where(n => indegree[n] == 0), StringComparer.Ordinal);
        var order = new List<string>(nodes.Count);

        while (ready.Count > 0)
        {
            var next = ready.Min!;
            ready.Remove(next);
            order.Add(next);

            foreach (var succ in successors[next])
            {
                if (--indegree[succ] == 0)
                {
                    ready.Add(succ);
                }
            }
        }

        if (order.Count != nodes.Count)
        {
            var remaining = nodes.Where(n => indegree[n] > 0).OrderBy(x => x, StringComparer.Ordinal);
            throw new DependencyCycleException(remaining);
        }

        return order;
    }

    public static bool HasCycle(IReadOnlyDictionary<string, IReadOnlyCollection<string>> edges)
    {
        try
        {
            TopologicalOrder(edges);
            return false;
        }
        catch (DependencyCycleException)
        {
            return true;
        }
    }

    /// <summary>
    /// The direct predecessors that must all be in <c>Succeeded</c> before <paramref name="node"/>
    /// may run (fan-in). Returns an empty set for a root node.
    /// </summary>
    public static IReadOnlyCollection<string> Dependencies(
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> edges,
        string node) =>
        edges.TryGetValue(node, out var deps) ? deps : [];
}

/// <summary>Raised when a dependency graph contains a cycle.</summary>
public sealed class DependencyCycleException(IEnumerable<string> nodesInCycle)
    : InvalidOperationException($"Dependency cycle detected involving: {string.Join(", ", nodesInCycle)}.")
{
    public IReadOnlyList<string> Nodes { get; } = nodesInCycle.ToList();
}
