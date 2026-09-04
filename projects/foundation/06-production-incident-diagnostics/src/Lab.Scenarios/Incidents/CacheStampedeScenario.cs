using System.Collections.Concurrent;
using System.Diagnostics;
using Lab.Diagnostics.Measurement;

namespace Lab.Scenarios.Incidents;

public sealed class CacheStampedeScenario : IIncidentScenario
{
    public string Id => "INC-010";

    public string Name => "Cache stampede on concurrent miss";

    public async Task<ScenarioReport> RunAsync(ScenarioRunOptions options, CancellationToken cancellationToken)
    {
        var operations = options.BoundedRequests(6, 40);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.Timeout);
        var token = budget.Token;
        var session = new MeasurementSession();
        var cache = new StampedeCache();
        var latency = new LatencyHistogram();

        var callers = Enumerable.Range(0, operations).Select(async _ =>
        {
            var stopwatch = Stopwatch.StartNew();
            var value = options.Mode == ScenarioMode.Broken
                ? await cache.GetBrokenAsync("shipment:TRK-9001", token)
                : await cache.GetSingleFlightAsync("shipment:TRK-9001", token);
            stopwatch.Stop();
            latency.Record(stopwatch.Elapsed);
            return value;
        });
        var values = await Task.WhenAll(callers).WaitAsync(token);
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
                ["originLoads"] = cache.OriginCalls.Count,
                ["coalescedWaiters"] = operations - cache.OriginCalls.Count,
                ["p95LatencyMilliseconds"] = Math.Round(latencySummary.P95Milliseconds, 2),
                ["distinctValuesReturned"] = values.Distinct(StringComparer.Ordinal).Count(),
                ["ttlPolicy"] = "200 ms base TTL plus deterministic 0-24 ms jitter"
            },
            Evidence =
            [
                $"OutboundCallCounter observed {cache.OriginCalls.Count} origin loads for {operations} simultaneous cache callers.",
                "Fixed mode uses ConcurrentDictionary<string, Lazy<Task<CacheEntry>>> execution-and-publication coalescing."
            ],
            Limitations =
            [
                "Single-flight state is process-local. A multi-node cache needs a distributed lock or a stale-while-revalidate strategy to prevent cross-node stampedes."
            ]
        };
    }

    private sealed class StampedeCache
    {
        private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Lazy<Task<CacheEntry>>> _inflight = new(StringComparer.Ordinal);

        public OutboundCallCounter OriginCalls { get; } = new();

        public async Task<string> GetBrokenAsync(string key, CancellationToken cancellationToken)
        {
            if (TryGetFresh(key, out var entry))
            {
                return entry.Value;
            }

            var loaded = await LoadOriginAsync(key, cancellationToken);
            _entries[key] = loaded;
            return loaded.Value;
        }

        public async Task<string> GetSingleFlightAsync(string key, CancellationToken cancellationToken)
        {
            if (TryGetFresh(key, out var entry))
            {
                return entry.Value;
            }

            var lazy = _inflight.GetOrAdd(
                key,
                cacheKey => new Lazy<Task<CacheEntry>>(
                    () => LoadOriginAsync(cacheKey, cancellationToken),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            try
            {
                var loaded = await lazy.Value;
                _entries[key] = loaded;
                return loaded.Value;
            }
            finally
            {
                if (lazy.IsValueCreated && lazy.Value.IsCompleted)
                {
                    _inflight.TryRemove(new KeyValuePair<string, Lazy<Task<CacheEntry>>>(key, lazy));
                }
            }
        }

        private bool TryGetFresh(string key, out CacheEntry entry) =>
            _entries.TryGetValue(key, out entry!) && entry.ExpiresAtUtc > DateTimeOffset.UtcNow;

        private async Task<CacheEntry> LoadOriginAsync(string key, CancellationToken cancellationToken)
        {
            OriginCalls.Increment();
            await Task.Delay(20, cancellationToken);
            var jitterMilliseconds = Math.Abs(StringComparer.Ordinal.GetHashCode(key)) % 25;
            return new CacheEntry(
                "northstar-shipment-state",
                DateTimeOffset.UtcNow.AddMilliseconds(200 + jitterMilliseconds));
        }
    }

    private sealed record CacheEntry(string Value, DateTimeOffset ExpiresAtUtc);
}
