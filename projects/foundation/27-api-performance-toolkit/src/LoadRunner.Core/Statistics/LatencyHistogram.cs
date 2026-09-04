namespace LoadRunner.Core.Statistics;

/// <summary>
/// A logarithmic bucketed histogram in the spirit of HdrHistogram, in pure managed C#.
/// Values are non-negative durations expressed in nanoseconds; the histogram guarantees a
/// worst-case relative precision equal to <see cref="RelativePrecision"/> across its range.
///
/// The bucket layout is fixed by two parameters:
///   * <paramref name="significantValueDigits"/> — number of decimal digits of precision.
///     3 → &lt;= 0.1% error; 2 → &lt;= 1% error; 4 → &lt;= 0.01% error.
///   * <paramref name="maxTrackableValueNanos"/> — the largest value the histogram can classify.
///     Values above this are still counted (in a sentinel bucket) but are clamped for
///     percentile queries and marked in the overflow counter.
///
/// The internal layout uses top-level "buckets" (powers of two) each subdivided into a fixed
/// number of linear sub-buckets. Bucket 0 uses the full sub-bucket range 0..subBucketCount-1;
/// each subsequent bucket only uses its top half (the bottom half is redundant with the
/// previous bucket) — hence the array size formula (bucketCount + 1) * subBucketHalfCount.
/// </summary>
public sealed class LatencyHistogram
{
    private readonly long[] _counts;
    private readonly int _subBucketCount;
    private readonly int _subBucketHalfCount;
    private readonly int _subBucketHalfCountMagnitude;
    private readonly int _subBucketMask;
    private readonly int _bucketCount;
    private readonly int _unitMagnitude = 0;

    public int SignificantValueDigits { get; }
    public long MaxTrackableValueNanos { get; }
    public double RelativePrecision { get; }
    public long TotalCount { get; private set; }
    public long OverflowCount { get; private set; }
    public long MinValueNanos { get; private set; } = long.MaxValue;
    public long MaxValueNanos { get; private set; }

    public LatencyHistogram(long maxTrackableValueNanos = 60_000_000_000, int significantValueDigits = 3)
    {
        if (significantValueDigits < 1 || significantValueDigits > 5)
            throw new ArgumentOutOfRangeException(nameof(significantValueDigits), "1..5 expected");
        if (maxTrackableValueNanos < 2)
            throw new ArgumentOutOfRangeException(nameof(maxTrackableValueNanos));

        SignificantValueDigits = significantValueDigits;
        MaxTrackableValueNanos = maxTrackableValueNanos;
        RelativePrecision = Math.Pow(10, -significantValueDigits);

        var largestValueWithSingleUnitResolution = (long)(2 * Math.Pow(10, significantValueDigits));
        var subBucketCountMagnitude = (int)Math.Ceiling(Math.Log2(largestValueWithSingleUnitResolution));
        _subBucketHalfCountMagnitude = subBucketCountMagnitude > 1 ? subBucketCountMagnitude - 1 : 0;
        _subBucketCount = 1 << (_subBucketHalfCountMagnitude + 1);
        _subBucketHalfCount = _subBucketCount / 2;
        _subBucketMask = _subBucketCount - 1;
        _bucketCount = GetBucketsNeededToCover(maxTrackableValueNanos);
        var countsArraySize = (_bucketCount + 1) * _subBucketHalfCount;
        _counts = new long[countsArraySize];
    }

    private int GetBucketsNeededToCover(long value)
    {
        long smallestUntrackable = _subBucketCount;
        var bucketsNeeded = 1;
        while (smallestUntrackable <= value)
        {
            if (smallestUntrackable > long.MaxValue / 2) { bucketsNeeded++; break; }
            smallestUntrackable <<= 1;
            bucketsNeeded++;
        }
        return bucketsNeeded;
    }

    public void Record(long valueNanos)
    {
        if (valueNanos < 0) throw new ArgumentOutOfRangeException(nameof(valueNanos));
        if (valueNanos > MaxTrackableValueNanos)
        {
            OverflowCount++;
            valueNanos = MaxTrackableValueNanos;
        }
        var idx = CountsIndexFor(valueNanos);
        _counts[idx]++;
        TotalCount++;
        if (valueNanos < MinValueNanos) MinValueNanos = valueNanos;
        if (valueNanos > MaxValueNanos) MaxValueNanos = valueNanos;
    }

    public void RecordDuration(TimeSpan value) => Record((long)(value.TotalNanoseconds));

    private int CountsIndexFor(long value)
    {
        var bucketIndex = GetBucketIndex(value);
        var subBucketIndex = GetSubBucketIndex(value, bucketIndex);
        return CountsIndex(bucketIndex, subBucketIndex);
    }

    private int GetBucketIndex(long value)
    {
        // Same trick as reference HdrHistogram:
        //   pow2Ceiling(value | subBucketMask) - unitMagnitude - (subBucketHalfCountMagnitude + 1)
        var combined = (ulong)value | (ulong)(long)_subBucketMask;
        var pow2Ceiling = 64 - System.Numerics.BitOperations.LeadingZeroCount(combined);
        return pow2Ceiling - _unitMagnitude - (_subBucketHalfCountMagnitude + 1);
    }

    private int GetSubBucketIndex(long value, int bucketIndex) =>
        (int)((ulong)value >> (bucketIndex + _unitMagnitude));

    private int CountsIndex(int bucketIndex, int subBucketIndex)
    {
        var bucketBaseIndex = (bucketIndex + 1) << _subBucketHalfCountMagnitude;
        var offset = subBucketIndex - _subBucketHalfCount;
        return bucketBaseIndex + offset;
    }

    private long ValueFromIndex(int bucketIndex, int subBucketIndex)
        => (long)subBucketIndex << (bucketIndex + _unitMagnitude);

    private long HighestEquivalentValueFromIndex(int bucketIndex, int subBucketIndex)
    {
        var value = ValueFromIndex(bucketIndex, subBucketIndex);
        var scale = 1L << (bucketIndex + _unitMagnitude);
        return value + scale - 1;
    }

    public long GetValueAtPercentile(double percentile)
    {
        if (TotalCount == 0) return 0;
        percentile = Math.Clamp(percentile, 0.0, 100.0);
        var countAtPercentile = (long)Math.Ceiling((percentile / 100.0) * TotalCount);
        if (countAtPercentile <= 0) countAtPercentile = 1;

        long totalToCurrentIndex = 0;
        for (var i = 0; i < _counts.Length; i++)
        {
            totalToCurrentIndex += _counts[i];
            if (totalToCurrentIndex >= countAtPercentile)
            {
                var (b, s) = DecodeIndex(i);
                return HighestEquivalentValueFromIndex(b, s);
            }
        }
        return MaxValueNanos;
    }

    private (int bucket, int sub) DecodeIndex(int index)
    {
        var bucketIndex = (index >> _subBucketHalfCountMagnitude) - 1;
        int subBucketIndex;
        if (bucketIndex < 0)
        {
            bucketIndex = 0;
            subBucketIndex = index;
        }
        else
        {
            subBucketIndex = (index & (_subBucketHalfCount - 1)) + _subBucketHalfCount;
        }
        return (bucketIndex, subBucketIndex);
    }

    public double GetMean()
    {
        if (TotalCount == 0) return 0;
        double total = 0;
        for (var i = 0; i < _counts.Length; i++)
        {
            if (_counts[i] == 0) continue;
            var (b, s) = DecodeIndex(i);
            var midpoint = HighestEquivalentValueFromIndex(b, s) - ((1L << (b + _unitMagnitude)) / 2);
            total += (double)_counts[i] * midpoint;
        }
        return total / TotalCount;
    }

    public double GetStdDev()
    {
        if (TotalCount == 0) return 0;
        var mean = GetMean();
        double geometricDeviationTotal = 0;
        for (var i = 0; i < _counts.Length; i++)
        {
            if (_counts[i] == 0) continue;
            var (b, s) = DecodeIndex(i);
            var midpoint = (double)(HighestEquivalentValueFromIndex(b, s) - ((1L << (b + _unitMagnitude)) / 2));
            var deviation = midpoint - mean;
            geometricDeviationTotal += deviation * deviation * _counts[i];
        }
        return Math.Sqrt(geometricDeviationTotal / TotalCount);
    }

    public void Merge(LatencyHistogram other)
    {
        if (other._counts.Length != _counts.Length)
            throw new InvalidOperationException("Histograms with different layouts cannot be merged");
        for (var i = 0; i < _counts.Length; i++) _counts[i] += other._counts[i];
        TotalCount += other.TotalCount;
        OverflowCount += other.OverflowCount;
        if (other.MinValueNanos < MinValueNanos) MinValueNanos = other.MinValueNanos;
        if (other.MaxValueNanos > MaxValueNanos) MaxValueNanos = other.MaxValueNanos;
    }
}
