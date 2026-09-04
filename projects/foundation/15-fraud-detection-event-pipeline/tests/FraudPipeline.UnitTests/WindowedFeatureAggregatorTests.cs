using FraudPipeline.Domain.Features;
using FraudPipeline.Domain.ValueObjects;

namespace FraudPipeline.UnitTests;

public class WindowedFeatureAggregatorTests
{
    private static readonly EntityId _card = EntityId.Of(EntityType.Card, "CARD00001");
    private static readonly DateTimeOffset _t0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static WindowedFeatureAggregator NewAgg(int sliceSeconds = 60, int bucketCount = 60 * 60)
        => new(_card, sliceSeconds, bucketCount);

    private static FeatureObservation Obs(DateTimeOffset at, decimal amount, string merchant = "M1", string country = "KE", string device = "D1")
        => new(_card, at, amount, merchant, country, device);

    [Fact]
    public void Aggregate_WithinSubWindow_ReturnsCountAndSum()
    {
        var a = NewAgg();
        a.Add(Obs(_t0.AddSeconds(1), 100));
        a.Add(Obs(_t0.AddSeconds(30), 200));
        a.Add(Obs(_t0.AddSeconds(59), 300));
        var agg = a.Aggregate(_t0.AddMinutes(1), windowSeconds: 60);
        Assert.Equal(3, agg.Count);
        Assert.Equal(600m, agg.AmountSum);
    }

    [Fact]
    public void Aggregate_EventsOutsideWindow_AreExcluded()
    {
        var a = NewAgg();
        a.Add(Obs(_t0, 100));
        a.Add(Obs(_t0.AddMinutes(2), 200));
        var agg = a.Aggregate(_t0.AddMinutes(2), windowSeconds: 60);
        Assert.Equal(1, agg.Count);
        Assert.Equal(200m, agg.AmountSum);
    }

    [Fact]
    public void Aggregate_ExactlyAtEdge_IsIncludedInWindow()
    {
        // slice = 60s. observation at t0+30. Aggregate at t0+90 with 60s window.
        // window covers (t0+30 .. t0+90]. Bucket starting at t0+0 has SliceStartTicks=t0+0 which is < start=t0+30, so excluded. Bucket at t0+60 is included.
        var a = NewAgg();
        a.Add(Obs(_t0.AddSeconds(30), 100));
        a.Add(Obs(_t0.AddSeconds(75), 200));
        var agg = a.Aggregate(_t0.AddSeconds(90), windowSeconds: 60);
        // Slice keys 0 and 60. Window (30, 90]. Slice 0 excluded, slice 60 included.
        Assert.Equal(1, agg.Count);
        Assert.Equal(200m, agg.AmountSum);
    }

    [Fact]
    public void Advance_EvictsOldBuckets()
    {
        // 1-min slices, 5 buckets => 5-minute window.
        var a = new WindowedFeatureAggregator(_card, sliceSeconds: 60, bucketCount: 5);
        a.Add(Obs(_t0, 100));
        a.Add(Obs(_t0.AddMinutes(1), 200));
        Assert.Equal(2, a.BucketCountLive);
        var evicted = a.Advance(_t0.AddMinutes(10));
        Assert.Equal(2, evicted);
        Assert.Equal(0, a.BucketCountLive);
    }

    [Fact]
    public void Aggregate_ManyEventsAcrossHour_ComputesAvgAndStddev()
    {
        var a = NewAgg();
        for (int i = 0; i < 20; i++) a.Add(Obs(_t0.AddSeconds(i * 30), 100 + i));
        var agg = a.Aggregate(_t0.AddMinutes(15), windowSeconds: 3600);
        Assert.Equal(20, agg.Count);
        Assert.InRange((double)agg.AmountAvg, 108, 112);
        Assert.True(agg.AmountStdDev > 0m);
    }

    [Fact]
    public void Aggregate_DistinctCountriesAndMerchants_AreUnioned()
    {
        var a = NewAgg();
        a.Add(Obs(_t0, 100, "M1", "KE", "D1"));
        a.Add(Obs(_t0.AddSeconds(15), 100, "M2", "US", "D2"));
        a.Add(Obs(_t0.AddSeconds(30), 100, "M1", "KE", "D2"));
        var agg = a.Aggregate(_t0.AddMinutes(1), windowSeconds: 60);
        Assert.Equal(2, agg.DistinctMerchants);
        Assert.Equal(2, agg.DistinctCountries);
        Assert.Equal(2, agg.DistinctDevices);
    }

    [Fact]
    public void OutOfOrderArrival_WithinRetention_StillAggregatesCorrectly()
    {
        var a = NewAgg();
        a.Add(Obs(_t0.AddMinutes(3), 300));
        a.Add(Obs(_t0.AddMinutes(1), 100));
        a.Add(Obs(_t0.AddMinutes(2), 200));
        var agg = a.Aggregate(_t0.AddMinutes(5), windowSeconds: 3600);
        Assert.Equal(3, agg.Count);
        Assert.Equal(600m, agg.AmountSum);
    }

    [Fact]
    public void Aggregate_SubWindowLargerThanRetention_Throws()
    {
        var a = new WindowedFeatureAggregator(_card, sliceSeconds: 60, bucketCount: 5);
        Assert.Throws<ArgumentOutOfRangeException>(() => a.Aggregate(_t0, windowSeconds: 3600));
    }
}
