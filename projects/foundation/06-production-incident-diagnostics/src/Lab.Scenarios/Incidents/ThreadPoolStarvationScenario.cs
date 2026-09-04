using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Lab.Diagnostics.Measurement;

namespace Lab.Scenarios.Incidents;

public sealed class ThreadPoolStarvationScenario : IIncidentScenario
{
    public string Id => "INC-006";

    public string Name => "Thread-pool starvation from blocking work";

    public async Task<ScenarioReport> RunAsync(ScenarioRunOptions options, CancellationToken cancellationToken)
    {
        var operations = options.BoundedRequests(8, 24);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.Timeout);
        var token = budget.Token;
        var session = new MeasurementSession();
        var queueDelay = new LatencyHistogram();
        var sampler = new ThreadPoolSampler();

        if (options.Mode == ScenarioMode.Broken)
        {
            using var constrainedRequestWorkers = new SemaphoreSlim(2, 2);
            var work = Enumerable.Range(0, operations).Select(_ =>
            {
                var queuedAt = Stopwatch.GetTimestamp();
                return Task.Run(() =>
                {
                    sampler.Sample();
                    constrainedRequestWorkers.Wait(token);
                    try
                    {
                        queueDelay.Record(Stopwatch.GetElapsedTime(queuedAt));
                        Thread.Sleep(14);
                    }
                    finally
                    {
                        constrainedRequestWorkers.Release();
                    }
                }, token);
            });
            await Task.WhenAll(work).WaitAsync(token);
        }
        else
        {
            var channel = Channel.CreateBounded<QueuedWork>(new BoundedChannelOptions(operations)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = true,
                SingleReader = false
            });

            using var workersReady = new CountdownEvent(4);
            using var startConsumption = new ManualResetEventSlim(initialState: false);
            var workers = Enumerable.Range(0, 4)
                .Select(_ => Task.Factory.StartNew(
                    () => ConsumeDedicatedWorker(channel.Reader, queueDelay, token, workersReady, startConsumption),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default))
                .ToArray();
            if (!workersReady.Wait(TimeSpan.FromSeconds(1), token))
            {
                throw new TimeoutException("Dedicated workers did not start within the scenario budget.");
            }

            for (var index = 0; index < operations; index++)
            {
                channel.Writer.TryWrite(new QueuedWork(Stopwatch.GetTimestamp()));
            }

            channel.Writer.TryComplete();
            startConsumption.Set();
            await Task.WhenAll(workers).WaitAsync(token);
            sampler.Sample();
        }

        var delay = queueDelay.Snapshot();
        var threadPool = sampler.Snapshot();
        var outcome = session.Complete();
        return new ScenarioReport
        {
            ScenarioId = Id,
            ScenarioName = Name,
            Mode = options.Mode,
            RequestedOperations = operations,
            StartedAtUtc = DateTimeOffset.UtcNow,
            ElapsedMilliseconds = outcome.Elapsed.TotalMilliseconds,
            Metrics = new Dictionary<string, object?>
            {
                ["queueDelayP50Milliseconds"] = Math.Round(delay.P50Milliseconds, 2),
                ["queueDelayP95Milliseconds"] = Math.Round(delay.P95Milliseconds, 2),
                ["queueDelayP99Milliseconds"] = Math.Round(delay.P99Milliseconds, 2),
                ["workerModel"] = options.Mode == ScenarioMode.Broken ? "ThreadPool + blocking capacity gate" : "bounded Channel + 4 dedicated long-running workers",
                ["peakBusyWorkerThreads"] = threadPool.PeakBusyWorkerThreads,
                ["boundedConcurrency"] = options.Mode == ScenarioMode.Broken ? 2 : 4
            },
            Evidence =
            [
                "Queue delay is measured from enqueue timestamp to the moment a work item starts consuming a worker slot.",
                "Fixed mode runs blocking work behind a bounded channel on dedicated long-running workers, preventing request workers from being held."
            ],
            Limitations =
            [
                "The capacity gate makes the starvation signal repeatable without changing process-wide ThreadPool limits."
            ]
        };
    }

    private static void ConsumeDedicatedWorker(
        ChannelReader<QueuedWork> reader,
        LatencyHistogram queueDelay,
        CancellationToken cancellationToken,
        CountdownEvent workersReady,
        ManualResetEventSlim startConsumption)
    {
        workersReady.Signal();
        startConsumption.Wait(cancellationToken);
        while (reader.TryRead(out var work))
        {
            cancellationToken.ThrowIfCancellationRequested();
            queueDelay.Record(Stopwatch.GetElapsedTime(work.QueuedAt));
            Thread.Sleep(14);
        }
    }

    private sealed record QueuedWork(long QueuedAt);
}
