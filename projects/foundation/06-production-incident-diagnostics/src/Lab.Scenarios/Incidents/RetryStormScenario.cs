using Lab.Diagnostics.Measurement;

namespace Lab.Scenarios.Incidents;

public sealed class RetryStormScenario : IIncidentScenario
{
    public string Id => "INC-008";

    public string Name => "Cascading retry storm";

    public async Task<ScenarioReport> RunAsync(ScenarioRunOptions options, CancellationToken cancellationToken)
    {
        var operations = options.BoundedRequests(3, 12);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.Timeout);
        var token = budget.Token;
        var session = new MeasurementSession();
        var calls = new OutboundCallCounter();
        var circuitRejected = 0;

        if (options.Mode == ScenarioMode.Broken)
        {
            for (var index = 0; index < operations; index++)
            {
                try
                {
                    await GatewayRetryAsync(calls, token);
                }
                catch (HttpRequestException)
                {
                    // The top-level caller observes a terminal failure after all three nested retry layers have amplified it.
                }
            }
        }
        else
        {
            var circuit = new CircuitBreaker(failureThreshold: 2);
            for (var index = 0; index < operations; index++)
            {
                if (!circuit.TryEnter())
                {
                    circuitRejected++;
                    continue;
                }

                for (var attempt = 1; attempt <= 2; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        await FailingDownstreamAsync(calls, token);
                    }
                    catch (HttpRequestException)
                    {
                        circuit.RecordFailure();
                        if (attempt < 2 && circuit.TryEnter())
                        {
                            await Task.Delay(1 + ((index + attempt) % 3), token);
                        }
                    }
                }
            }
        }

        var outcome = session.Complete();
        var expectedBaselineCalls = Math.Max(1, operations);
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
                ["actualDownstreamCalls"] = calls.Count,
                ["callsPerLogicalRequest"] = Math.Round((double)calls.Count / operations, 2),
                ["amplificationVersusOneAttempt"] = Math.Round((double)calls.Count / expectedBaselineCalls, 2),
                ["circuitRejectedRequests"] = circuitRejected,
                ["policy"] = options.Mode == ScenarioMode.Broken
                    ? "three attempts at gateway, service, and repository layers"
                    : "two-call global retry budget, circuit breaker threshold 2, deterministic jitter"
            },
            Evidence =
            [
                $"OutboundCallCounter observed {calls.Count} actual failing downstream calls.",
                "Broken composition is 3 × 3 × 3 attempts per logical request when the dependency always fails."
            ],
            Limitations =
            [
                "The circuit breaker is intentionally small and in-process for reproducibility; distributed breakers need shared state and careful partitioning."
            ]
        };
    }

    private static Task GatewayRetryAsync(OutboundCallCounter calls, CancellationToken cancellationToken) =>
        RetryThreeAsync(() => ServiceRetryAsync(calls, cancellationToken), cancellationToken);

    private static Task ServiceRetryAsync(OutboundCallCounter calls, CancellationToken cancellationToken) =>
        RetryThreeAsync(() => RepositoryRetryAsync(calls, cancellationToken), cancellationToken);

    private static Task RepositoryRetryAsync(OutboundCallCounter calls, CancellationToken cancellationToken) =>
        RetryThreeAsync(() => FailingDownstreamAsync(calls, cancellationToken), cancellationToken);

    private static async Task RetryThreeAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await action();
                return;
            }
            catch (HttpRequestException) when (attempt < 3)
            {
            }
            catch (HttpRequestException)
            {
                throw;
            }
        }
    }

    private static Task FailingDownstreamAsync(OutboundCallCounter calls, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        calls.Increment();
        return Task.FromException(new HttpRequestException("Synthetic transient downstream failure."));
    }

    private sealed class CircuitBreaker(int failureThreshold)
    {
        private readonly object _gate = new();
        private int _failures;
        private bool _open;

        public bool TryEnter()
        {
            lock (_gate)
            {
                return !_open;
            }
        }

        public void RecordFailure()
        {
            lock (_gate)
            {
                _failures++;
                if (_failures >= failureThreshold)
                {
                    _open = true;
                }
            }
        }
    }
}
