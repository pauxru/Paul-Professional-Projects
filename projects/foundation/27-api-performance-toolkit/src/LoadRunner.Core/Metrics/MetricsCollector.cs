using System.Collections.Concurrent;
using LoadRunner.Core.Scenarios;
using LoadRunner.Core.Statistics;

namespace LoadRunner.Core.Metrics;

/// <summary>
/// Collects request samples during a run and folds them into per-step and aggregate
/// statistics. Samples are appended lock-free via a <see cref="ConcurrentQueue{T}"/>;
/// aggregation runs post-hoc against the frozen queue snapshot to keep the hot path fast.
///
/// The collector produces two views of latency:
///   * <b>Service latency</b> — measured from the moment the runner actually issues the
///     HTTP request. This is the classic "response time" number reported by most tools.
///   * <b>Intended latency</b> — measured from the moment the scheduled request was
///     <i>supposed to</i> start according to the open-model arrival plan. When a slow
///     server backs up the runner, service latency looks fine while intended latency
///     explodes. This is the classic "coordinated omission" correction that makes the
///     numbers defensible.
/// </summary>
public sealed class MetricsCollector
{
    private readonly ConcurrentQueue<RequestSample> _samples = new();
    private readonly int _histogramPrecision;

    public MetricsCollector(int histogramPrecision = 3)
    {
        _histogramPrecision = histogramPrecision;
    }

    public void Record(RequestSample sample) => _samples.Enqueue(sample);

    public IReadOnlyCollection<RequestSample> Samples => _samples;

    public RunSnapshot Snapshot(DateTimeOffset runStart, DateTimeOffset runEnd, TimeSpan? warmup = null)
    {
        var warmupCutoff = warmup.HasValue
            ? runStart.Add(warmup.Value).ToUnixTimeMilliseconds()
            : long.MinValue;

        var perStep = new Dictionary<string, StepAccumulator>(StringComparer.OrdinalIgnoreCase);
        var aggregate = new StepAccumulator("__aggregate__", _histogramPrecision);
        var timeSeries = new SortedDictionary<long, TimeBucket>();
        long totalOmitted = 0;

        foreach (var sample in _samples)
        {
            if (sample.CompletedUnixMs < warmupCutoff) { totalOmitted++; continue; }

            if (!perStep.TryGetValue(sample.StepName, out var acc))
            {
                acc = new StepAccumulator(sample.StepName, _histogramPrecision);
                perStep[sample.StepName] = acc;
            }
            acc.Add(sample);
            aggregate.Add(sample);

            var bucketSec = sample.CompletedUnixMs / 1000;
            if (!timeSeries.TryGetValue(bucketSec, out var bucket))
                timeSeries[bucketSec] = bucket = new TimeBucket();
            bucket.Add(sample);
        }

        var series = new List<TimeSeriesPoint>(timeSeries.Count);
        foreach (var (sec, bucket) in timeSeries)
        {
            series.Add(new TimeSeriesPoint(
                DateTimeOffset.FromUnixTimeSeconds(sec),
                bucket.Count,
                bucket.Errors,
                bucket.LatencyMsExact.Percentile(50),
                bucket.LatencyMsExact.Percentile(95),
                bucket.LatencyMsExact.Percentile(99)));
        }

        return new RunSnapshot(
            runStart,
            runEnd,
            warmup ?? TimeSpan.Zero,
            aggregate.ToStats(),
            perStep.Values.Select(a => a.ToStats()).ToList(),
            series,
            totalOmitted);
    }

    private sealed class StepAccumulator
    {
        public string Name { get; }
        public LatencyHistogram ServiceHistogram { get; }
        public LatencyHistogram IntendedHistogram { get; }
        public ExactPercentiles ServiceExactMs { get; } = new();
        public ExactPercentiles IntendedExactMs { get; } = new();
        public long Count { get; private set; }
        public long Errors { get; private set; }
        public long ConnectionErrors { get; private set; }
        public long TimeoutErrors { get; private set; }
        public long Http4xx { get; private set; }
        public long Http5xx { get; private set; }
        public long AssertionErrors { get; private set; }
        public long BytesReceived { get; private set; }
        public long FirstUnixMs { get; private set; } = long.MaxValue;
        public long LastUnixMs { get; private set; }

        public StepAccumulator(string name, int precision)
        {
            Name = name;
            ServiceHistogram = new LatencyHistogram(60_000_000_000L, precision);
            IntendedHistogram = new LatencyHistogram(600_000_000_000L, precision);
        }

        public void Add(RequestSample sample)
        {
            Count++;
            BytesReceived += sample.BytesReceived;
            ServiceHistogram.Record(sample.ServiceLatencyNs);
            IntendedHistogram.Record(sample.IntendedLatencyNs);
            ServiceExactMs.Add(sample.ServiceLatencyNs / 1_000_000.0);
            IntendedExactMs.Add(sample.IntendedLatencyNs / 1_000_000.0);
            if (sample.Error != ErrorKind.None) Errors++;
            switch (sample.Error)
            {
                case ErrorKind.Connection: ConnectionErrors++; break;
                case ErrorKind.Timeout: TimeoutErrors++; break;
                case ErrorKind.Http4xx: Http4xx++; break;
                case ErrorKind.Http5xx: Http5xx++; break;
                case ErrorKind.AssertionFailure: AssertionErrors++; break;
            }
            if (sample.ActualStartUnixMs < FirstUnixMs) FirstUnixMs = sample.ActualStartUnixMs;
            if (sample.CompletedUnixMs > LastUnixMs) LastUnixMs = sample.CompletedUnixMs;
        }

        public LatencyStats Percentiles(bool useExact)
        {
            if (useExact)
            {
                return new LatencyStats(
                    ServiceExactMs.Percentile(50),
                    ServiceExactMs.Percentile(75),
                    ServiceExactMs.Percentile(90),
                    ServiceExactMs.Percentile(95),
                    ServiceExactMs.Percentile(99),
                    ServiceExactMs.Percentile(99.9),
                    ServiceExactMs.Min(),
                    ServiceExactMs.Max(),
                    ServiceExactMs.Mean(),
                    ServiceExactMs.StdDev());
            }
            return new LatencyStats(
                ServiceHistogram.GetValueAtPercentile(50) / 1_000_000.0,
                ServiceHistogram.GetValueAtPercentile(75) / 1_000_000.0,
                ServiceHistogram.GetValueAtPercentile(90) / 1_000_000.0,
                ServiceHistogram.GetValueAtPercentile(95) / 1_000_000.0,
                ServiceHistogram.GetValueAtPercentile(99) / 1_000_000.0,
                ServiceHistogram.GetValueAtPercentile(99.9) / 1_000_000.0,
                ServiceHistogram.MinValueNanos / 1_000_000.0,
                ServiceHistogram.MaxValueNanos / 1_000_000.0,
                ServiceHistogram.GetMean() / 1_000_000.0,
                ServiceHistogram.GetStdDev() / 1_000_000.0);
        }

        public LatencyStats IntendedPercentiles(bool useExact)
        {
            if (useExact)
            {
                return new LatencyStats(
                    IntendedExactMs.Percentile(50),
                    IntendedExactMs.Percentile(75),
                    IntendedExactMs.Percentile(90),
                    IntendedExactMs.Percentile(95),
                    IntendedExactMs.Percentile(99),
                    IntendedExactMs.Percentile(99.9),
                    IntendedExactMs.Min(),
                    IntendedExactMs.Max(),
                    IntendedExactMs.Mean(),
                    IntendedExactMs.StdDev());
            }
            return new LatencyStats(
                IntendedHistogram.GetValueAtPercentile(50) / 1_000_000.0,
                IntendedHistogram.GetValueAtPercentile(75) / 1_000_000.0,
                IntendedHistogram.GetValueAtPercentile(90) / 1_000_000.0,
                IntendedHistogram.GetValueAtPercentile(95) / 1_000_000.0,
                IntendedHistogram.GetValueAtPercentile(99) / 1_000_000.0,
                IntendedHistogram.GetValueAtPercentile(99.9) / 1_000_000.0,
                IntendedHistogram.MinValueNanos / 1_000_000.0,
                IntendedHistogram.MaxValueNanos / 1_000_000.0,
                IntendedHistogram.GetMean() / 1_000_000.0,
                IntendedHistogram.GetStdDev() / 1_000_000.0);
        }

        public StepStats ToStats()
        {
            var useExact = ServiceExactMs.Count > 0 && !ServiceExactMs.Overflowed;
            var durationSec = Math.Max(1, (LastUnixMs - FirstUnixMs) / 1000.0);
            var throughput = Count / durationSec;
            var errorRate = Count > 0 ? (double)Errors / Count : 0;
            return new StepStats(
                Name,
                Count,
                Errors,
                errorRate,
                throughput,
                ConnectionErrors,
                TimeoutErrors,
                Http4xx,
                Http5xx,
                AssertionErrors,
                BytesReceived,
                Percentiles(useExact),
                IntendedPercentiles(useExact));
        }
    }

    private sealed class TimeBucket
    {
        public long Count;
        public long Errors;
        public ExactPercentiles LatencyMsExact { get; } = new(maxSamples: 100_000);

        public void Add(RequestSample sample)
        {
            Count++;
            if (sample.Error != ErrorKind.None) Errors++;
            LatencyMsExact.Add(sample.ServiceLatencyNs / 1_000_000.0);
        }
    }
}

public sealed record LatencyStats(
    double P50Ms,
    double P75Ms,
    double P90Ms,
    double P95Ms,
    double P99Ms,
    double P999Ms,
    double MinMs,
    double MaxMs,
    double MeanMs,
    double StdDevMs);

public sealed record StepStats(
    string StepName,
    long Count,
    long Errors,
    double ErrorRate,
    double ThroughputRps,
    long ConnectionErrors,
    long TimeoutErrors,
    long Http4xx,
    long Http5xx,
    long AssertionErrors,
    long BytesReceived,
    LatencyStats Service,
    LatencyStats Intended);

public sealed record TimeSeriesPoint(
    DateTimeOffset TimestampUtc,
    long Count,
    long Errors,
    double P50Ms,
    double P95Ms,
    double P99Ms);

public sealed record RunSnapshot(
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    TimeSpan Warmup,
    StepStats Aggregate,
    IReadOnlyList<StepStats> PerStep,
    IReadOnlyList<TimeSeriesPoint> TimeSeries,
    long OmittedWarmupSamples);
