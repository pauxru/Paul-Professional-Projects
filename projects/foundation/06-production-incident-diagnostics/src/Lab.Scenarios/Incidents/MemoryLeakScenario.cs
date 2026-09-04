using Lab.Diagnostics.Measurement;

namespace Lab.Scenarios.Incidents;

public sealed class MemoryLeakScenario : IIncidentScenario
{
    public string Id => "INC-004";

    public string Name => "Static memory retention leak";

    public async Task<ScenarioReport> RunAsync(ScenarioRunOptions options, CancellationToken cancellationToken)
    {
        var iterations = options.BoundedRequests(8, 80);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.Timeout);
        var token = budget.Token;
        RetentionSink.Clear();
        _ = MemorySnapshot.Capture(forceCollection: true);
        var session = new MeasurementSession();

        try
        {
            for (var index = 0; index < iterations; index++)
            {
                token.ThrowIfCancellationRequested();
                var payload = new byte[32 * 1024];
                payload[0] = (byte)index;

                if (options.Mode == ScenarioMode.Broken)
                {
                    RetentionSink.Subscribe(payload);
                }
                else
                {
                    await ProcessAndReleaseAsync(payload, token);
                }
            }

            var outcome = session.Complete(forceCollection: true);
            var retained = RetentionSink.Count;
            return new ScenarioReport
            {
                ScenarioId = Id,
                ScenarioName = Name,
                Mode = options.Mode,
                RequestedOperations = iterations,
                StartedAtUtc = DateTimeOffset.UtcNow,
                ElapsedMilliseconds = outcome.Elapsed.TotalMilliseconds,
                Metrics = new Dictionary<string, object?>
                {
                    ["retainedPayloads"] = retained,
                    ["heapDeltaBytesAfterForcedGc"] = outcome.Delta.ManagedHeapBytes,
                    ["allocatedBytes"] = outcome.Delta.AllocatedBytes,
                    ["payloadBytesPerRequest"] = 32 * 1024,
                    ["staticHandlerSubscriptions"] = RetentionSink.HandlerCount
                },
                Evidence =
                [
                    $"GC.GetTotalMemory(true) delta: {outcome.Delta.ManagedHeapBytes} bytes.",
                    $"GC.GetTotalAllocatedBytes delta: {outcome.Delta.AllocatedBytes} bytes."
                ],
                Limitations =
                [
                    "The retained-object count is the primary deterministic signal; GC heap deltas can vary across runtime versions."
                ]
            };
        }
        finally
        {
            RetentionSink.Clear();
        }
    }

    private static Task ProcessAndReleaseAsync(byte[] payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = payload[0];
        return Task.CompletedTask;
    }

    private static class RetentionSink
    {
        private static readonly List<byte[]> Payloads = [];
        private static EventHandler? _pulse;

        public static int Count
        {
            get
            {
                lock (Payloads)
                {
                    return Payloads.Count;
                }
            }
        }

        public static int HandlerCount => _pulse?.GetInvocationList().Length ?? 0;

        public static void Subscribe(byte[] payload)
        {
            lock (Payloads)
            {
                Payloads.Add(payload);
                _pulse += (_, _) => GC.KeepAlive(payload);
            }
        }

        public static void Clear()
        {
            lock (Payloads)
            {
                Payloads.Clear();
                _pulse = null;
            }
        }
    }
}
