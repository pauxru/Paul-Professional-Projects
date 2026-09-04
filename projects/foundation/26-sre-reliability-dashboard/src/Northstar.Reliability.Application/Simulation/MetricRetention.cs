using Northstar.Reliability.Domain.Telemetry;

namespace Northstar.Reliability.Application.Simulation;

public sealed record MetricRetentionPolicy(TimeSpan RawRetention, TimeSpan HourlyRetention);

public sealed record MetricRetentionResult(
    IReadOnlyList<MetricSample> RetainedRawSamples,
    IReadOnlyList<MetricSample> HourlyRollups,
    int ExpiredSamples);

public static class MetricRetentionEngine
{
    public static MetricRetentionResult Apply(
        IEnumerable<MetricSample> samples,
        MetricRetentionPolicy policy,
        DateTimeOffset now)
    {
        var all = samples.ToArray();
        var rawCutoff = now.Subtract(policy.RawRetention);
        var hourlyCutoff = now.Subtract(policy.HourlyRetention);
        var retainedRaw = all.Where(sample => sample.Timestamp >= rawCutoff).ToArray();
        var rollupCandidates = all
            .Where(sample => sample.Timestamp < rawCutoff && sample.Timestamp >= hourlyCutoff)
            .ToArray();
        var expired = all.Length - retainedRaw.Length - rollupCandidates.Length;
        var rollups = rollupCandidates
            .GroupBy(sample => new RollupKey(
                sample.ServiceSlug,
                sample.Endpoint,
                sample.Region,
                sample.Tier,
                TruncateToHour(sample.Timestamp)))
            .Select(group => ToHourlyRollup(group.Key, group.ToArray()))
            .OrderBy(sample => sample.Timestamp)
            .ToArray();
        return new MetricRetentionResult(retainedRaw, rollups, expired);
    }

    private static MetricSample ToHourlyRollup(RollupKey key, IReadOnlyList<MetricSample> samples)
    {
        var totalRequests = samples.Sum(sample => sample.Requests);
        var p50 = WeightedAverage(samples, sample => sample.P50LatencyMilliseconds, totalRequests);
        var p95 = WeightedAverage(samples, sample => sample.P95LatencyMilliseconds, totalRequests);
        var buckets = samples
            .SelectMany(sample => sample.LatencyHistogram)
            .GroupBy(bucket => bucket.UpperBoundMilliseconds)
            .Select(group => new LatencyHistogramBucket(group.Key, group.Sum(bucket => bucket.Count)))
            .OrderBy(bucket => bucket.UpperBoundMilliseconds)
            .ToArray();

        return MetricSample.Create(
            key.ServiceSlug,
            key.Hour,
            key.Endpoint,
            key.Region,
            key.Tier,
            totalRequests,
            samples.Sum(sample => sample.Errors),
            samples.Sum(sample => sample.LatencyGoodRequests),
            samples.Sum(sample => sample.QualityGoodEvents),
            samples.Sum(sample => sample.QualityValidEvents),
            samples.Sum(sample => sample.FreshnessGoodEvents),
            samples.Sum(sample => sample.FreshnessValidEvents),
            samples.Sum(sample => sample.ProbeGoodMinutes),
            samples.Sum(sample => sample.ProbeTotalMinutes),
            p50,
            Math.Max(p50, p95),
            buckets,
            MetricResolution.Hour);
    }

    private static double WeightedAverage(
        IEnumerable<MetricSample> samples,
        Func<MetricSample, double> selector,
        long totalRequests)
    {
        if (totalRequests == 0)
        {
            return 0d;
        }

        return samples.Sum(sample => selector(sample) * sample.Requests) / totalRequests;
    }

    private static DateTimeOffset TruncateToHour(DateTimeOffset timestamp)
    {
        var utc = timestamp.UtcDateTime;
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }

    private sealed record RollupKey(
        string ServiceSlug,
        string Endpoint,
        string Region,
        string Tier,
        DateTimeOffset Hour);
}
