using LoadRunner.Core.Statistics;
using Xunit;

namespace LoadRunner.UnitTests.Statistics;

public class ExactPercentilesTests
{
    [Fact]
    public void Percentile_MatchesHandComputedValues_ForSmallDataSet()
    {
        // Nearest-rank definition: rank = ceil(p/100 * n)
        // Values: 10,20,30,40,50,60,70,80,90,100 (n=10)
        // p50 => rank 5 => 50
        // p90 => rank 9 => 90
        // p99 => rank 10 => 100
        var samples = new double[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };
        var ep = new ExactPercentiles();
        foreach (var v in samples) ep.Add(v);

        Assert.Equal(50, ep.Percentile(50));
        Assert.Equal(90, ep.Percentile(90));
        Assert.Equal(100, ep.Percentile(99));
        Assert.Equal(10, ep.Percentile(0.1));
    }

    [Fact]
    public void PercentileZero_And_HundredEdgeValues_AreClampedInsideRange()
    {
        var ep = new ExactPercentiles();
        foreach (var v in new double[] { 1, 2, 3, 4, 5 }) ep.Add(v);
        Assert.Equal(1, ep.Percentile(0));
        Assert.Equal(5, ep.Percentile(100));
    }

    [Fact]
    public void Statistics_MatchClassicalFormulas()
    {
        var ep = new ExactPercentiles();
        var samples = new double[] { 2, 4, 4, 4, 5, 5, 7, 9 };
        foreach (var v in samples) ep.Add(v);
        Assert.Equal(5, ep.Mean(), precision: 6);
        Assert.Equal(2, ep.StdDev(), precision: 6);
        Assert.Equal(2, ep.Min());
        Assert.Equal(9, ep.Max());
    }

    [Fact]
    public void Percentiles_HandleSingleSample_Gracefully()
    {
        var ep = new ExactPercentiles();
        ep.Add(42);
        Assert.Equal(42, ep.Percentile(50));
        Assert.Equal(42, ep.Percentile(99.9));
    }
}
