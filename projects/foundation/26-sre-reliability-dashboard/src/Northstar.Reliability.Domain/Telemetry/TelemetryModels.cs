using Northstar.Reliability.Domain.Common;

namespace Northstar.Reliability.Domain.Telemetry;

public enum MetricResolution
{
    Minute,
    FiveMinutes,
    Hour
}

public sealed record LatencyHistogramBucket(decimal UpperBoundMilliseconds, long Count);

public sealed record MetricSample(
    Guid Id,
    string ServiceSlug,
    DateTimeOffset Timestamp,
    string Endpoint,
    string Region,
    string Tier,
    long Requests,
    long Errors,
    long LatencyGoodRequests,
    long QualityGoodEvents,
    long QualityValidEvents,
    long FreshnessGoodEvents,
    long FreshnessValidEvents,
    long ProbeGoodMinutes,
    long ProbeTotalMinutes,
    double P50LatencyMilliseconds,
    double P95LatencyMilliseconds,
    IReadOnlyList<LatencyHistogramBucket> LatencyHistogram,
    MetricResolution Resolution)
{
    public static MetricSample Create(
        string serviceSlug,
        DateTimeOffset timestamp,
        string endpoint,
        string region,
        string tier,
        long requests,
        long errors,
        long latencyGoodRequests,
        long qualityGoodEvents,
        long qualityValidEvents,
        long freshnessGoodEvents,
        long freshnessValidEvents,
        long probeGoodMinutes,
        long probeTotalMinutes,
        double p50LatencyMilliseconds,
        double p95LatencyMilliseconds,
        IEnumerable<LatencyHistogramBucket>? latencyHistogram = null,
        MetricResolution resolution = MetricResolution.Minute)
    {
        if (string.IsNullOrWhiteSpace(serviceSlug) || string.IsNullOrWhiteSpace(endpoint) ||
            string.IsNullOrWhiteSpace(region) || string.IsNullOrWhiteSpace(tier))
        {
            throw new DomainRuleViolationException("Service, endpoint, region, and tier are required.");
        }

        var sample = new MetricSample(
            Guid.NewGuid(),
            serviceSlug.Trim().ToLowerInvariant(),
            timestamp,
            endpoint.Trim(),
            region.Trim(),
            tier.Trim(),
            requests,
            errors,
            latencyGoodRequests,
            qualityGoodEvents,
            qualityValidEvents,
            freshnessGoodEvents,
            freshnessValidEvents,
            probeGoodMinutes,
            probeTotalMinutes,
            p50LatencyMilliseconds,
            p95LatencyMilliseconds,
            latencyHistogram?.ToArray() ?? [],
            resolution);
        sample.EnsureValid();
        return sample;
    }

    public void EnsureValid()
    {
        var values = new[]
        {
            Requests, Errors, LatencyGoodRequests, QualityGoodEvents, QualityValidEvents,
            FreshnessGoodEvents, FreshnessValidEvents, ProbeGoodMinutes, ProbeTotalMinutes
        };
        if (values.Any(value => value < 0))
        {
            throw new DomainRuleViolationException("Metric counters cannot be negative.");
        }

        if (Errors > Requests || LatencyGoodRequests > Requests ||
            QualityGoodEvents > QualityValidEvents || FreshnessGoodEvents > FreshnessValidEvents ||
            ProbeGoodMinutes > ProbeTotalMinutes)
        {
            throw new DomainRuleViolationException("Metric good/error counters exceed their valid event totals.");
        }

        if (P50LatencyMilliseconds < 0 || P95LatencyMilliseconds < 0 || P50LatencyMilliseconds > P95LatencyMilliseconds)
        {
            throw new DomainRuleViolationException("Latency percentiles must be non-negative with p50 no greater than p95.");
        }

        if (LatencyHistogram.Any(bucket => bucket.UpperBoundMilliseconds <= 0 || bucket.Count < 0))
        {
            throw new DomainRuleViolationException("Latency histogram buckets must have positive bounds and non-negative counts.");
        }
    }

    public long RequestsBelow(decimal thresholdMilliseconds) =>
        LatencyHistogram.Count == 0
            ? LatencyGoodRequests
            : LatencyHistogram
                .Where(bucket => bucket.UpperBoundMilliseconds <= thresholdMilliseconds)
                .Sum(bucket => bucket.Count);
}
