using LoadRunner.Core.Statistics;
using Xunit;

namespace LoadRunner.UnitTests.Statistics;

public class LatencyHistogramTests
{
    [Fact]
    public void RecordedValueIsWithinRelativePrecisionOfTruePercentile()
    {
        // 3 significant digits => <= 0.1% error
        var histogram = new LatencyHistogram(maxTrackableValueNanos: 60_000_000_000, significantValueDigits: 3);
        var rng = new Random(1);
        var samples = new List<long>(50_000);
        for (var i = 0; i < 50_000; i++)
        {
            // synthetic latency 1..500ms in nanoseconds
            var v = (long)(rng.NextDouble() * 500_000_000 + 1_000_000);
            samples.Add(v);
            histogram.Record(v);
        }
        samples.Sort();
        long TruePercentile(double p)
        {
            var rank = (int)Math.Ceiling((p / 100.0) * samples.Count);
            return samples[rank - 1];
        }

        foreach (var p in new[] { 50.0, 90.0, 95.0, 99.0, 99.9 })
        {
            var truth = TruePercentile(p);
            var estimate = histogram.GetValueAtPercentile(p);
            var relErr = Math.Abs(estimate - truth) / (double)truth;
            Assert.True(relErr <= 0.005,
                $"p{p}: relative error {relErr:P3} exceeds 0.5% tolerance (truth={truth}, est={estimate})");
        }
    }

    [Fact]
    public void EmptyHistogram_ReturnsZeroForAllPercentiles()
    {
        var histogram = new LatencyHistogram();
        Assert.Equal(0, histogram.GetValueAtPercentile(50));
        Assert.Equal(0, histogram.GetValueAtPercentile(99));
        Assert.Equal(0, histogram.TotalCount);
    }

    [Fact]
    public void SingleValueHistogram_ReportsBucketedMaxValue()
    {
        var histogram = new LatencyHistogram();
        histogram.Record(1_234_567);
        var estimate = histogram.GetValueAtPercentile(99);
        // Within precision bound
        Assert.InRange(estimate, 1_234_500, 1_240_000);
        Assert.Equal(1, histogram.TotalCount);
    }

    [Fact]
    public void OverflowValues_AreCountedAndClampedToMaxTrackable()
    {
        var max = 1_000_000L;
        var histogram = new LatencyHistogram(maxTrackableValueNanos: max, significantValueDigits: 2);
        histogram.Record(max * 5);
        Assert.Equal(1, histogram.OverflowCount);
        Assert.True(histogram.MaxValueNanos <= max);
    }

    [Fact]
    public void MergeCombines_TotalCountsAndPercentiles()
    {
        var a = new LatencyHistogram();
        var b = new LatencyHistogram();
        for (var i = 1L; i <= 1000; i++) a.Record(i * 100_000);
        for (var i = 1L; i <= 1000; i++) b.Record(i * 200_000);
        a.Merge(b);
        Assert.Equal(2000, a.TotalCount);
    }
}
