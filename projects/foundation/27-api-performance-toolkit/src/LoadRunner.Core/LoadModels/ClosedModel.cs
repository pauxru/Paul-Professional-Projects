using LoadRunner.Core.Http;
using LoadRunner.Core.Metrics;
using LoadRunner.Core.Scenarios;
using LoadRunner.Core.Time;

namespace LoadRunner.Core.LoadModels;

public interface ILoadModel
{
    Task RunAsync(RunContext context, CancellationToken cancellationToken);
}

public sealed record RunContext(
    ScenarioDefinition Scenario,
    IHttpRequestExecutor Executor,
    MetricsCollector Metrics,
    IClock Clock,
    CsvFeeder? Feeder,
    DateTimeOffset RunStartedUtc,
    Action<string>? Log = null);

/// <summary>
/// Constant-VU (closed model): a fixed pool of virtual users each executes the scenario
/// steps sequentially, sleeping think-times between requests, for the configured
/// duration. Requests only start when the previous request of the same VU has finished —
/// this is exactly what a closed-model load test should do, and it's why the RPS you
/// observe depends on the server's response time (throughput is emergent).
/// </summary>
public sealed class ClosedModel : ILoadModel
{
    public async Task RunAsync(RunContext context, CancellationToken cancellationToken)
    {
        var plan = context.Scenario.Load;
        var vus = plan.ConstantVUs ?? 1;
        var duration = plan.Duration ?? TimeSpan.FromSeconds(10);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(duration);
        var tasks = new Task[vus];
        for (var i = 0; i < vus; i++)
        {
            var vuId = i;
            tasks[i] = Task.Run(() => VirtualUser.RunAsync(vuId, context, deadline.Token), CancellationToken.None);
        }
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected on deadline */ }
    }
}

/// <summary>
/// Ramping-VU: piecewise-linear interpolation between stages. Each stage names a target
/// VU count and a duration; VUs are spawned or retired at a constant rate through the
/// stage so the population smoothly follows the plan.
/// </summary>
public sealed class RampingClosedModel : ILoadModel
{
    public async Task RunAsync(RunContext context, CancellationToken cancellationToken)
    {
        var plan = context.Scenario.Load;
        var stages = plan.Stages ?? throw new InvalidOperationException("Stages required");
        var maxVUs = plan.MaxVUs ?? stages.Max(s => s.TargetLoad);

        var vuTasks = new List<Task>();
        var vuCts = new List<CancellationTokenSource>();
        var currentVUs = 0;
        var totalDuration = TimeSpan.Zero;
        foreach (var stage in stages) totalDuration += stage.Duration;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(totalDuration);

        try
        {
            foreach (var stage in stages)
            {
                var target = stage.TargetLoad;
                var delta = target - currentVUs;
                var perStepDelay = delta == 0
                    ? stage.Duration
                    : TimeSpan.FromMilliseconds(stage.Duration.TotalMilliseconds / Math.Abs(delta));
                if (delta >= 0)
                {
                    for (var i = 0; i < delta; i++)
                    {
                        if (deadline.IsCancellationRequested) return;
                        var vuId = currentVUs + i;
                        var cts = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                        vuCts.Add(cts);
                        vuTasks.Add(Task.Run(() => VirtualUser.RunAsync(vuId, context, cts.Token), CancellationToken.None));
                        if (i < delta - 1)
                            await Task.Delay(perStepDelay, deadline.Token).ConfigureAwait(false);
                    }
                    currentVUs = target;
                    // Hold the rest of the stage if we finished spawning early
                    // (delta small relative to duration).
                }
                else
                {
                    var toKill = -delta;
                    for (var i = 0; i < toKill; i++)
                    {
                        var idx = vuCts.Count - 1;
                        if (idx < 0) break;
                        vuCts[idx].Cancel();
                        vuCts.RemoveAt(idx);
                        if (i < toKill - 1)
                            await Task.Delay(perStepDelay, deadline.Token).ConfigureAwait(false);
                    }
                    currentVUs = target;
                }
            }
            await Task.WhenAll(vuTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally { foreach (var c in vuCts) c.Dispose(); }
    }
}
