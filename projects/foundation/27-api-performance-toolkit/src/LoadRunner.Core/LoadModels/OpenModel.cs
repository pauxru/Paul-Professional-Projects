using System.Threading.Channels;
using LoadRunner.Core.Metrics;
using LoadRunner.Core.Scenarios;

namespace LoadRunner.Core.LoadModels;

/// <summary>
/// Open model: requests are scheduled by an arrival plan independent of the server's
/// response time. Each scheduled request keeps its <c>intendedStartUnixMs</c> so slow
/// responses show up as long "intended latencies" — the coordinated-omission correction.
///
/// A pool of worker tasks dispatches the queued requests. If the pool is saturated,
/// requests <i>queue</i> — they are NOT dropped and NOT delayed silently. That queueing
/// delay is exactly the phenomenon a real service would surface, and it is what a naive
/// closed-model tester would miss.
/// </summary>
public sealed class OpenModel : ILoadModel
{
    private readonly int _workerPoolSize;

    public OpenModel(int workerPoolSize = 512)
    {
        _workerPoolSize = workerPoolSize;
    }

    public async Task RunAsync(RunContext context, CancellationToken cancellationToken)
    {
        var plan = context.Scenario.Load;
        var rate = plan.ConstantArrivalRatePerSec ?? throw new InvalidOperationException("ConstantArrivalRatePerSec required");
        var duration = plan.Duration ?? throw new InvalidOperationException("Duration required");
        await RunAtRateAsync(context, rate, duration, cancellationToken).ConfigureAwait(false);
    }

    public async Task RunAtRateAsync(RunContext context, int ratePerSec, TimeSpan duration, CancellationToken cancellationToken)
    {
        var intervalMs = 1000.0 / ratePerSec;
        var queue = Channel.CreateUnbounded<ScheduledRequest>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true
        });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(duration + TimeSpan.FromSeconds(30)); // grace period so queued requests can finish

        var workers = new Task[Math.Min(_workerPoolSize, Math.Max(ratePerSec * 4, 32))];
        for (var i = 0; i < workers.Length; i++)
        {
            var wid = i;
            workers[i] = Task.Run(() => WorkerLoop(wid, queue.Reader, context, deadline.Token), CancellationToken.None);
        }

        var scheduler = Task.Run(async () =>
        {
            var startTs = DateTimeOffset.UtcNow;
            var totalRequests = (long)Math.Ceiling(ratePerSec * duration.TotalSeconds);
            for (var i = 0L; i < totalRequests; i++)
            {
                if (deadline.IsCancellationRequested) break;
                var scheduled = startTs.AddMilliseconds(intervalMs * i);
                var delay = scheduled - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                {
                    try { await Task.Delay(delay, deadline.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
                await queue.Writer.WriteAsync(
                    new ScheduledRequest(scheduled.ToUnixTimeMilliseconds()),
                    deadline.Token).ConfigureAwait(false);
            }
            queue.Writer.TryComplete();
        }, CancellationToken.None);

        try { await Task.WhenAll(scheduler).ConfigureAwait(false); } catch (OperationCanceledException) { }
        // drain until deadline
        try { await Task.WhenAll(workers).ConfigureAwait(false); } catch (OperationCanceledException) { }
    }

    private static async Task WorkerLoop(int workerId, ChannelReader<ScheduledRequest> reader,
        RunContext context, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var req in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var variables = VirtualUser.BuildVariables(workerId, context);
                foreach (var step in context.Scenario.Steps)
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    var intended = req.IntendedStartUnixMs;
                    RequestSample sample;
                    try
                    {
                        sample = await context.Executor.ExecuteAsync(
                            step, variables, workerId, intended, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { return; }
                    context.Metrics.Record(sample);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private sealed record ScheduledRequest(long IntendedStartUnixMs);
}

/// <summary>
/// Ramping arrival rate: piecewise-linear ramp between stages. Each stage has a target
/// per-second rate and a duration; the model interpolates the rate every 100ms.
/// </summary>
public sealed class RampingOpenModel : ILoadModel
{
    public async Task RunAsync(RunContext context, CancellationToken cancellationToken)
    {
        var plan = context.Scenario.Load;
        var stages = plan.Stages ?? throw new InvalidOperationException("Stages required");
        var open = new OpenModel();
        var currentRate = 0;
        foreach (var stage in stages)
        {
            var target = stage.TargetLoad;
            var stepMs = 100;
            var steps = Math.Max(1, (int)(stage.Duration.TotalMilliseconds / stepMs));
            for (var i = 0; i < steps; i++)
            {
                if (cancellationToken.IsCancellationRequested) return;
                var rate = (int)Math.Round(currentRate + (target - currentRate) * ((i + 1.0) / steps));
                if (rate <= 0) rate = 1;
                await open.RunAtRateAsync(context, rate, TimeSpan.FromMilliseconds(stepMs), cancellationToken).ConfigureAwait(false);
            }
            currentRate = target;
        }
    }
}
