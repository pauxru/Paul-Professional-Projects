using FraudPipeline.Domain.ValueObjects;

namespace FraudPipeline.Domain.Features;

/// <summary>
/// Per-entity sliding-window feature aggregator.
///
/// Design:
///   - Time is divided into fixed-width buckets (SliceSeconds wide).
///   - We keep a ring of BucketCount buckets covering (BucketCount * SliceSeconds) seconds.
///   - Observations are added into the bucket keyed by (occurredAt / SliceSeconds).
///   - Advance(now) evicts buckets older than the window.
///   - Aggregates over sub-windows (1m/5m/1h/24h) are computed by summing buckets whose
///     SliceStart is within (now - windowSeconds..now]. The DB is never queried.
///
/// This is O(BucketCount) for a full evict and O(1) amortised for Add. Distinct-set
/// aggregates union the per-bucket hash-sets; the sets are bounded by real cardinality.
/// </summary>
public sealed class WindowedFeatureAggregator
{
    public EntityId Entity { get; }
    public int SliceSeconds { get; }
    public int BucketCount { get; }
    public long WindowSeconds => (long)SliceSeconds * BucketCount;

    private readonly Dictionary<long, FeatureBucket> _buckets = new();
    private DateTimeOffset _lastAdvance;

    public WindowedFeatureAggregator(EntityId entity, int sliceSeconds, int bucketCount)
    {
        if (sliceSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(sliceSeconds));
        if (bucketCount <= 0) throw new ArgumentOutOfRangeException(nameof(bucketCount));
        Entity = entity;
        SliceSeconds = sliceSeconds;
        BucketCount = bucketCount;
    }

    private long SliceKey(DateTimeOffset at)
    {
        // Anchor buckets on epoch seconds / SliceSeconds — deterministic across processes.
        var epochSeconds = at.ToUnixTimeSeconds();
        return epochSeconds - (((epochSeconds % SliceSeconds) + SliceSeconds) % SliceSeconds);
    }

    public void Add(FeatureObservation obs)
    {
        var key = SliceKey(obs.At);
        if (!_buckets.TryGetValue(key, out var bucket))
        {
            bucket = new FeatureBucket(key);
            _buckets[key] = bucket;
        }
        bucket.Add(obs.Amount, obs.MerchantId, obs.CountryIso2, obs.DeviceId, obs.WasDecline, obs.WasChargeback);
        _lastAdvance = obs.At;
    }

    /// <summary>
    /// Evict buckets older than the retention window relative to <paramref name="now"/>.
    /// Callers should invoke this periodically or before large queries; Add does not
    /// implicitly evict so that late-arriving observations aren't silently dropped by
    /// a background sweep — see AddWithEviction for the strict variant.
    /// </summary>
    public int Advance(DateTimeOffset now)
    {
        _lastAdvance = now;
        var cutoff = now.ToUnixTimeSeconds() - WindowSeconds;
        var stale = new List<long>();
        foreach (var k in _buckets.Keys)
        {
            if (k < cutoff) stale.Add(k);
        }
        foreach (var k in stale) _buckets.Remove(k);
        return stale.Count;
    }

    public int BucketCountLive => _buckets.Count;

    /// <summary>
    /// Aggregate over a sub-window ending at <paramref name="now"/> and covering the
    /// last <paramref name="windowSeconds"/> seconds. The sub-window may not exceed
    /// the aggregator's WindowSeconds.
    /// </summary>
    public WindowAggregate Aggregate(DateTimeOffset now, long windowSeconds)
    {
        if (windowSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(windowSeconds));
        if (windowSeconds > WindowSeconds) throw new ArgumentOutOfRangeException(nameof(windowSeconds), "sub-window exceeds aggregator retention");
        var end = now.ToUnixTimeSeconds();
        var start = end - windowSeconds;
        int count = 0, declines = 0, chargebacks = 0;
        decimal sum = 0m, sqSum = 0m;
        var merchants = new HashSet<string>(StringComparer.Ordinal);
        var countries = new HashSet<string>(StringComparer.Ordinal);
        var devices = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in _buckets.Values)
        {
            // Bucket qualifies if its slice-start falls inside [start, end].
            // Rationale: aggregation is at slice granularity; a bucket whose start
            // equals the window start still represents observations in the window.
            if (b.SliceStartTicks < start) continue;
            if (b.SliceStartTicks > end) continue;
            count += b.Count;
            sum += b.AmountSum;
            sqSum += b.AmountSquareSum;
            declines += b.DeclineCount;
            chargebacks += b.ChargebackCount;
            foreach (var m in b.DistinctMerchants) merchants.Add(m);
            foreach (var c in b.DistinctCountries) countries.Add(c);
            foreach (var d in b.DistinctDevices) devices.Add(d);
        }
        return new WindowAggregate(count, sum, sqSum, declines, chargebacks, merchants.Count, countries.Count, devices.Count);
    }

    public IReadOnlyDictionary<long, FeatureBucket> Buckets => _buckets;
}

public readonly record struct WindowAggregate(
    int Count,
    decimal AmountSum,
    decimal AmountSquareSum,
    int DeclineCount,
    int ChargebackCount,
    int DistinctMerchants,
    int DistinctCountries,
    int DistinctDevices)
{
    public decimal AmountAvg => Count == 0 ? 0m : AmountSum / Count;

    public decimal AmountStdDev
    {
        get
        {
            if (Count < 2) return 0m;
            var mean = AmountAvg;
            var meanSq = AmountSquareSum / Count;
            var variance = meanSq - mean * mean;
            if (variance <= 0m) return 0m;
            return (decimal)Math.Sqrt((double)variance);
        }
    }
}
