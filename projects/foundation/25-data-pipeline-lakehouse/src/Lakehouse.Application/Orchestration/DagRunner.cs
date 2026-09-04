using System.Collections.Concurrent;
using System.Diagnostics;
using Lakehouse.Application.Abstractions;
using Lakehouse.Application.Observability;
using Lakehouse.Application.Quality;

namespace Lakehouse.Application.Orchestration;

/// <summary>Thrown when a run is requested for a window that is already executing (concurrency control).</summary>
public sealed class OverlappingRunException(string window)
    : Exception($"A run for window '{window}' is already in progress; overlapping runs are not allowed.");

/// <summary>
/// Executes a <see cref="Dag"/> in topological order with per-task retries, a circuit breaker that
/// blocks all downstream tasks when a quality gate trips, concurrency control preventing overlapping
/// runs of the same window, and OpenTelemetry spans per task. Supports partial re-runs (a set of tasks
/// and their downstream) and backfilling a range of windows. Every run is persisted to run history.
/// </summary>
public sealed class DagRunner(IRunHistoryStore history, IClock clock)
{
    private readonly ConcurrentDictionary<string, byte> _running = new(StringComparer.Ordinal);

    /// <summary>Run the whole DAG, or only the given tasks and their downstream closure.</summary>
    public RunRecord Run(Dag dag, RunContext ctx, IEnumerable<string>? onlyTasks = null)
    {
        if (!_running.TryAdd(ctx.Window, 0))
            throw new OverlappingRunException(ctx.Window);
        try
        {
            return Execute(dag, ctx, onlyTasks);
        }
        finally
        {
            _running.TryRemove(ctx.Window, out _);
        }
    }

    /// <summary>Backfill a sequence of windows, running the full DAG for each. Returns one record per window.</summary>
    public IReadOnlyList<RunRecord> Backfill(Dag dag, IEnumerable<string> windows, Func<string, RunContext> contextFor)
        => windows.Select(w => Run(dag, contextFor(w))).ToList();

    private RunRecord Execute(Dag dag, RunContext ctx, IEnumerable<string>? onlyTasks)
    {
        var startedAt = clock.UtcNow;
        var order = dag.TopologicalOrder();
        var selected = onlyTasks is null
            ? new HashSet<string>(order, StringComparer.Ordinal)
            : new HashSet<string>(dag.DownstreamClosure(onlyTasks), StringComparer.Ordinal);

        var state = new Dictionary<string, TaskState>(StringComparer.Ordinal);
        var results = new List<TaskResult>();

        foreach (var id in order.Where(selected.Contains))
        {
            var task = dag.Tasks[id];

            // Block if any in-scope dependency did not succeed.
            var blockingDep = task.DependsOn.FirstOrDefault(d =>
                selected.Contains(d) && state.TryGetValue(d, out var s) && s is TaskState.Failed or TaskState.Blocked);
            if (blockingDep is not null)
            {
                state[id] = TaskState.Blocked;
                results.Add(new TaskResult(id, TaskState.Blocked, 0, 0, 0, 0, 0,
                    Note: $"blocked by upstream '{blockingDep}'"));
                continue;
            }

            results.Add(RunTask(task, ctx, state));
        }

        var record = new RunRecord(ctx.RunId, ctx.Window, startedAt, clock.UtcNow, results);
        history.Save(record);
        return record;
    }

    private TaskResult RunTask(PipelineTask task, RunContext ctx, Dictionary<string, TaskState> state)
    {
        using var activity = Telemetry.Source.StartActivity($"task:{task.Id}", ActivityKind.Internal);
        activity?.SetTag("lakehouse.run_id", ctx.RunId);
        activity?.SetTag("lakehouse.window", ctx.Window);

        var sw = Stopwatch.StartNew();
        var attempts = 0;
        Exception? last = null;

        while (attempts <= task.MaxRetries)
        {
            attempts++;
            try
            {
                var step = task.Run(ctx);
                sw.Stop();
                state[task.Id] = TaskState.Succeeded;
                activity?.SetTag("lakehouse.rows_out", step.RowsOut);
                activity?.SetTag("lakehouse.rows_quarantined", step.Quarantined);
                activity?.SetStatus(ActivityStatusCode.Ok);
                return new TaskResult(task.Id, TaskState.Succeeded, step.RowsIn, step.RowsOut,
                    step.Quarantined, attempts, sw.Elapsed.TotalMilliseconds, Note: step.Note);
            }
            catch (CircuitBreakerException ex)
            {
                // A tripped quality gate is a terminal, non-retryable failure that blocks promotion.
                sw.Stop();
                state[task.Id] = TaskState.Failed;
                activity?.SetStatus(ActivityStatusCode.Error, "circuit breaker tripped");
                return new TaskResult(task.Id, TaskState.Failed, 0, 0, 0, attempts,
                    sw.Elapsed.TotalMilliseconds, Error: ex.Message, Note: "circuit breaker");
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        sw.Stop();
        state[task.Id] = TaskState.Failed;
        activity?.SetStatus(ActivityStatusCode.Error, last?.Message);
        return new TaskResult(task.Id, TaskState.Failed, 0, 0, 0, attempts,
            sw.Elapsed.TotalMilliseconds, Error: last?.Message);
    }
}
