using System.Diagnostics;
using System.Net;
using System.Net.Http;
using Lab.Diagnostics.Measurement;

namespace Lab.Scenarios.Incidents;

public sealed class DownstreamTimeoutScenario : IIncidentScenario
{
    public string Id => "INC-007";

    public string Name => "Downstream timeout and request pile-up";

    public async Task<ScenarioReport> RunAsync(ScenarioRunOptions options, CancellationToken cancellationToken)
    {
        var operations = options.BoundedRequests(8, 30);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.Timeout);
        var token = budget.Token;
        var session = new MeasurementSession();
        var dependency = new SlowDependencyHandler(TimeSpan.FromMilliseconds(70));
        using var client = new HttpClient(dependency, disposeHandler: true)
        {
            Timeout = options.Mode == ScenarioMode.Broken
                ? Timeout.InfiniteTimeSpan
                : TimeSpan.FromMilliseconds(20)
        };
        var latency = new LatencyHistogram();
        var completed = 0;
        var timedOut = 0;
        var work = new List<Task>(operations);

        for (var index = 0; index < operations; index++)
        {
            work.Add(InvokeDependencyAsync(client, latency, () => Interlocked.Increment(ref completed), () => Interlocked.Increment(ref timedOut), token));
            await Task.Delay(5, token);
        }

        await Task.WhenAll(work).WaitAsync(token);
        await Task.Delay(5, token);

        var latencySummary = latency.Snapshot();
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
                ["completedResponses"] = completed,
                ["timeoutResponses"] = timedOut,
                ["p50LatencyMilliseconds"] = Math.Round(latencySummary.P50Milliseconds, 2),
                ["p95LatencyMilliseconds"] = Math.Round(latencySummary.P95Milliseconds, 2),
                ["peakDependencyPileUp"] = dependency.PeakActive,
                ["configuredClientTimeout"] = options.Mode == ScenarioMode.Broken ? "none (global scenario budget only)" : "20 ms",
                ["simulatedDependencyDelayMilliseconds"] = 70
            },
            Evidence =
            [
                "The in-process HttpMessageHandler observes cancellation tokens from HttpClient.",
                $"Peak concurrent downstream calls was {dependency.PeakActive}."
            ],
            Limitations =
            [
                "This models a slow dependency without using a network service; socket, DNS, and proxy failure signatures require a production-like environment."
            ]
        };
    }

    private static async Task InvokeDependencyAsync(
        HttpClient client,
        LatencyHistogram latency,
        Action markCompleted,
        Action markTimedOut,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await client.GetAsync("https://dependency.invalid/shipments", cancellationToken);
            response.EnsureSuccessStatusCode();
            markCompleted();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            markTimedOut();
        }
        finally
        {
            stopwatch.Stop();
            latency.Record(stopwatch.Elapsed);
        }
    }

    private sealed class SlowDependencyHandler(TimeSpan delay) : HttpMessageHandler
    {
        private int _active;
        private int _peakActive;

        public int PeakActive => Volatile.Read(ref _peakActive);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            SetMax(ref _peakActive, active);
            try
            {
                await Task.Delay(delay, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"status\":\"ok\"}")
                };
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private static void SetMax(ref int target, int candidate)
        {
            while (true)
            {
                var observed = Volatile.Read(ref target);
                if (observed >= candidate || Interlocked.CompareExchange(ref target, candidate, observed) == observed)
                {
                    return;
                }
            }
        }
    }
}
