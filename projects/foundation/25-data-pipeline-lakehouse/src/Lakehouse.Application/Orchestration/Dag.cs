namespace Lakehouse.Application.Orchestration;

/// <summary>
/// A dependency DAG of pipeline tasks. Provides deterministic topological ordering (with cycle and
/// missing-dependency detection) and the downstream closure of a set of tasks — the basis for partial
/// re-runs ("re-run this task and everything that depends on it").
/// </summary>
public sealed class Dag
{
    private readonly Dictionary<string, PipelineTask> _tasks = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, PipelineTask> Tasks => _tasks;

    public Dag Add(PipelineTask task)
    {
        if (!_tasks.TryAdd(task.Id, task))
            throw new InvalidOperationException($"Duplicate task id '{task.Id}'.");
        return this;
    }

    public Dag Add(string id, Func<RunContext, Pipelines.StepResult> run, int maxRetries = 0, params string[] dependsOn)
        => Add(new PipelineTask { Id = id, Run = run, MaxRetries = maxRetries, DependsOn = dependsOn });

    /// <summary>Kahn's algorithm — a stable topological order; throws on a missing dependency or a cycle.</summary>
    public IReadOnlyList<string> TopologicalOrder()
    {
        var indegree = _tasks.Keys.ToDictionary(k => k, _ => 0, StringComparer.Ordinal);
        foreach (var t in _tasks.Values)
            foreach (var dep in t.DependsOn)
            {
                if (!_tasks.ContainsKey(dep))
                    throw new InvalidOperationException($"Task '{t.Id}' depends on unknown task '{dep}'.");
                indegree[t.Id]++;
            }

        // Deterministic: process ready tasks in id order.
        var ready = new SortedSet<string>(indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key), StringComparer.Ordinal);
        var order = new List<string>();
        while (ready.Count > 0)
        {
            var id = ready.Min!;
            ready.Remove(id);
            order.Add(id);
            foreach (var t in _tasks.Values.Where(t => t.DependsOn.Contains(id)))
                if (--indegree[t.Id] == 0)
                    ready.Add(t.Id);
        }

        if (order.Count != _tasks.Count)
            throw new InvalidOperationException("Cycle detected in pipeline DAG.");
        return order;
    }

    /// <summary>The given tasks plus every task that (transitively) depends on them.</summary>
    public IReadOnlyCollection<string> DownstreamClosure(IEnumerable<string> roots)
    {
        var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var t in _tasks.Values)
            foreach (var dep in t.DependsOn)
                (dependents.TryGetValue(dep, out var list) ? list : dependents[dep] = new List<string>()).Add(t.Id);

        var closure = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        foreach (var r in roots)
        {
            if (!_tasks.ContainsKey(r)) throw new InvalidOperationException($"Unknown task '{r}'.");
            stack.Push(r);
        }
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!closure.Add(id)) continue;
            if (dependents.TryGetValue(id, out var deps))
                foreach (var d in deps) stack.Push(d);
        }
        return closure;
    }
}
