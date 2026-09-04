using JobScheduler.Domain;
using JobScheduler.Domain.Entities;

namespace JobScheduler.Application.Services;

/// <summary>The decision taken after a run fails or times out.</summary>
public sealed record RetryDecision(bool ShouldRetry, DateTimeOffset? NextScheduledAt, string Reason)
{
    public static RetryDecision DeadLetter(string reason) => new(false, null, reason);
    public static RetryDecision Retry(DateTimeOffset at, string reason) => new(true, at, reason);
}

/// <summary>
/// Pure policy that decides whether a failed run should be retried (and when) or dead-lettered.
/// Encapsulates poison detection: a run that has used its whole attempt budget, or failed with a
/// non-retriable error, is dead-lettered rather than retried forever.
/// </summary>
public static class RetryDecider
{
    /// <summary>
    /// <paramref name="completedAttempts"/> is the number of attempts already made (run.AttemptCount).
    /// <paramref name="jitterSample"/> lets tests pin the jittered delay.
    /// </summary>
    public static RetryDecision Decide(
        int completedAttempts,
        RetryPolicy policy,
        DateTimeOffset now,
        bool retriable,
        double? jitterSample = null)
    {
        if (!retriable)
        {
            return RetryDecision.DeadLetter("Handler reported a non-retriable (permanent) failure.");
        }

        if (!policy.ShouldRetry(completedAttempts))
        {
            return RetryDecision.DeadLetter(
                $"Exhausted retry budget after {completedAttempts}/{policy.MaxAttempts} attempts (poison).");
        }

        var delay = policy.NextDelay(completedAttempts, jitterSample);
        return RetryDecision.Retry(now + delay, $"Retry {completedAttempts + 1}/{policy.MaxAttempts} in {delay.TotalSeconds:0.###}s.");
    }
}

/// <summary>
/// Pure DAG readiness resolver used for dependency chains, fan-in and fan-out. Given the edge set
/// (node -> its dependencies), which nodes have succeeded, and which are already
/// enqueued/running/done, returns the nodes that are now ready to run (all dependencies succeeded).
/// </summary>
public static class DependencyResolver
{
    public static IReadOnlyList<string> ReadyToRun(
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> edges,
        ISet<string> succeeded,
        ISet<string> alreadyScheduled)
    {
        // Validate the graph first so a cycle is surfaced rather than silently stalling.
        if (DagValidator.HasCycle(edges))
        {
            throw new DependencyCycleException(edges.Keys);
        }

        var ready = new List<string>();
        foreach (var (node, deps) in edges)
        {
            if (succeeded.Contains(node) || alreadyScheduled.Contains(node))
            {
                continue;
            }
            if (deps.Count == 0)
            {
                continue; // roots are seeded explicitly, not by this fan-out resolver
            }
            if (deps.All(succeeded.Contains))
            {
                ready.Add(node);
            }
        }

        ready.Sort(StringComparer.Ordinal);
        return ready;
    }

    /// <summary>Root nodes (no dependencies) — the entry points that seed a workflow.</summary>
    public static IReadOnlyList<string> Roots(IReadOnlyDictionary<string, IReadOnlyCollection<string>> edges)
    {
        var roots = edges.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList();
        roots.Sort(StringComparer.Ordinal);
        return roots;
    }
}
