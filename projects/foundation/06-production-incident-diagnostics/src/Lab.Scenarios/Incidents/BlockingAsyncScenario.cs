using System.Diagnostics;
using Lab.Diagnostics.Measurement;

namespace Lab.Scenarios.Incidents;

public sealed class BlockingAsyncScenario : IIncidentScenario
{
    public string Id => "INC-005";

    public string Name => "Sync-over-async request blocking";

    public async Task<ScenarioReport> RunAsync(ScenarioRunOptions options, CancellationToken cancellationToken)
    {
        var operations = options.BoundedRequests(8, 32);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.Timeout);
        var token = budget.Token;
        var session = new MeasurementSession();
        var sampler = new ThreadPoolSampler();
        var latencies = new LatencyHistogram();
        var requests = new RequestCounter();
        long blockedWorkerMilliseconds = 0;

        if (options.Mode == ScenarioMode.Broken)
        {
            var work = Enumerable.Range(0, operations).Select(_ => Task.Run(() =>
            {
                var stopwatch = Stopwatch.StartNew();
                requests.MarkStarted();
                sampler.Sample();

                // This is intentionally the faulty path: it blocks a worker while an asynchronous I/O-shaped operation runs.
                var blockingStopwatch = Stopwatch.StartNew();
                Task.Delay(TimeSpan.FromMilliseconds(40), token).Wait(token);
                blockingStopwatch.Stop();
                Interlocked.Add(ref blockedWorkerMilliseconds, (long)blockingStopwatch.Elapsed.TotalMilliseconds);

                stopwatch.Stop();
                latencies.Record(stopwatch.Elapsed);
                requests.MarkCompleted();
                sampler.Sample();
            }, token));
            await Task.WhenAll(work).WaitAsync(token);
        }
        else
        {
            var work = Enumerable.Range(0, operations).Select(async _ =>
            {
                var stopwatch = Stopwatch.StartNew();
                requests.MarkStarted();
                sampler.Sample();
                await Task.Delay(TimeSpan.FromMilliseconds(40), token);
                stopwatch.Stop();
                latencies.Record(stopwatch.Elapsed);
                requests.MarkCompleted();
                sampler.Sample();
            });
            await Task.WhenAll(work).WaitAsync(token);
        }

        var latency = latencies.Snapshot();
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
                ["completedRequests"] = requests.Completed,
                ["requestsPerSecond"] = Math.Round(requests.Completed / Math.Max(outcome.Elapsed.TotalSeconds, 0.001), 2),
                ["p95LatencyMilliseconds"] = Math.Round(latency.P95Milliseconds, 2),
                ["peakBusyWorkerThreads"] = threadPool.PeakBusyWorkerThreads,
                ["minimumAvailableWorkerThreads"] = threadPool.MinimumAvailableWorkerThreads,
                ["threadPoolSampleCount"] = threadPool.SampleCount,
                ["blockedWorkerMilliseconds"] = blockedWorkerMilliseconds,
                ["syncWaitUsed"] = options.Mode == ScenarioMode.Broken
            },
            Evidence =
            [
                "ThreadPool.GetAvailableThreads was sampled at request entry and exit.",
                "Broken mode calls Task.Delay(...).Wait(...) on ThreadPool work items; fixed mode awaits the same delay."
            ],
            Limitations =
            [
                "Thread-pool injection is runtime and host dependent. The robust signal is occupied worker threads, not an absolute requests-per-second target."
            ]
        };
    }
}
