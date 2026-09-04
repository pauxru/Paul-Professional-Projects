using JobScheduler.Application.Abstractions;
using JobScheduler.Domain;
using JobScheduler.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace JobScheduler.Application.Services;

/// <summary>
/// Drives simple DAG workflows. Definitions declare <c>DependsOn</c> (upstream job names). When an
/// upstream run succeeds, dependents whose full fan-in has completed within the same workflow
/// (correlation) instance are enqueued. Cycles are rejected at definition time via
/// <see cref="ValidateNoCycleAsync"/>.
/// </summary>
public sealed class DagOrchestrator(
    IJobDefinitionStore definitions,
    IJobRunStore runs,
    IClock clock,
    ILogger<DagOrchestrator> logger)
{
    /// <summary>Builds the edge map (job name -> its dependency names) across all definitions.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyCollection<string>> BuildEdges(IEnumerable<JobDefinition> defs)
    {
        var edges = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);
        foreach (var d in defs)
        {
            edges[d.Name] = d.DependsOn;
        }
        return edges;
    }

    /// <summary>Throws <see cref="DependencyCycleException"/> if the definition set contains a cycle.</summary>
    public async Task ValidateNoCycleAsync(CancellationToken ct)
    {
        var all = await definitions.ListAllAsync(ct);
        DagValidator.TopologicalOrder(BuildEdges(all));
    }

    /// <summary>
    /// Called after a run reaches Succeeded. Enqueues any dependents whose dependencies have all
    /// succeeded in the same workflow. Returns the number of dependent runs enqueued.
    /// </summary>
    public async Task<int> OnRunSucceededAsync(JobRun succeeded, CancellationToken ct)
    {
        var workflow = succeeded.CorrelationId;
        var all = await definitions.ListAllAsync(ct);
        var dependents = all.Where(d => d.Enabled && d.DependsOn.Contains(succeeded.JobName, StringComparer.Ordinal));

        int enqueued = 0;
        foreach (var dep in dependents)
        {
            if (await runs.AnyRunInWorkflowAsync(dep.Name, workflow, ct))
            {
                continue; // already scheduled/ran in this workflow (idempotent fan-in)
            }

            bool allUpstreamSucceeded = true;
            foreach (var upstream in dep.DependsOn)
            {
                if (!await runs.SucceededInWorkflowAsync(upstream, workflow, ct))
                {
                    allUpstreamSucceeded = false;
                    break;
                }
            }

            if (!allUpstreamSucceeded)
            {
                continue;
            }

            var now = clock.UtcNow;
            var key = $"{dep.Id}:wf:{workflow}";
            var run = JobRun.Create(dep, now, now, key, workflow, "dag");
            await runs.AddAsync(run, ct);
            enqueued++;
            logger.LogInformation(
                "DAG: enqueued dependent {Dependent} after {Upstream} in workflow {Workflow}.",
                dep.Name, succeeded.JobName, workflow);
        }

        if (enqueued > 0)
        {
            await runs.SaveChangesAsync(ct);
        }
        return enqueued;
    }
}
