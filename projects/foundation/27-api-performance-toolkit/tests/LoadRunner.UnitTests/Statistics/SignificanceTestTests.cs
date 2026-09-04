using LoadRunner.Core.Statistics;
using Xunit;

namespace LoadRunner.UnitTests.Statistics;

public class SignificanceTestTests
{
    [Fact]
    public void MannWhitney_OnIdenticalDistributions_ReportsNoSignificantChange()
    {
        var rng = new Random(1);
        var a = new double[500];
        var b = new double[500];
        for (var i = 0; i < 500; i++) { a[i] = SampleNormal(rng, 100, 10); b[i] = SampleNormal(rng, 100, 10); }
        var result = SignificanceTest.MannWhitneyU(a, b);
        Assert.Equal(SignificanceTest.Verdict.NoSignificantChange, result.Verdict);
        Assert.True(result.PValue > 0.01);
    }

    [Fact]
    public void MannWhitney_OnClearlyDifferentDistributions_ReportsRegression()
    {
        var rng = new Random(2);
        var a = new double[500];
        var b = new double[500];
        for (var i = 0; i < 500; i++) { a[i] = SampleNormal(rng, 100, 10); b[i] = SampleNormal(rng, 150, 10); }
        var result = SignificanceTest.MannWhitneyU(a, b);
        Assert.Equal(SignificanceTest.Verdict.Regressed, result.Verdict);
        Assert.True(result.PValue < 1e-5);
    }

    [Fact]
    public void MannWhitney_OnClearlyImprovedDistributions_ReportsImprovement()
    {
        var rng = new Random(3);
        var a = new double[500];
        var b = new double[500];
        for (var i = 0; i < 500; i++) { a[i] = SampleNormal(rng, 200, 15); b[i] = SampleNormal(rng, 120, 10); }
        var result = SignificanceTest.MannWhitneyU(a, b);
        Assert.Equal(SignificanceTest.Verdict.Improved, result.Verdict);
    }

    [Fact]
    public void Bootstrap_OnIdenticalSamples_BracketsZero()
    {
        var rng = new Random(4);
        var a = new double[200];
        var b = new double[200];
        for (var i = 0; i < 200; i++) { a[i] = SampleNormal(rng, 100, 8); b[i] = SampleNormal(rng, 100, 8); }
        var result = SignificanceTest.BootstrapMedianDiff(a, b, iterations: 800);
        Assert.Equal(SignificanceTest.Verdict.NoSignificantChange, result.Verdict);
        Assert.True(result.CiLow < 0 && result.CiHigh > 0, $"CI [{result.CiLow}, {result.CiHigh}] should straddle zero");
    }

    [Fact]
    public void Bootstrap_OnRegressedSamples_ProducesPositiveCi()
    {
        var rng = new Random(5);
        var a = new double[400];
        var b = new double[400];
        for (var i = 0; i < 400; i++) { a[i] = SampleNormal(rng, 100, 6); b[i] = SampleNormal(rng, 130, 6); }
        var result = SignificanceTest.BootstrapMedianDiff(a, b, iterations: 800);
        Assert.Equal(SignificanceTest.Verdict.Regressed, result.Verdict);
        Assert.True(result.CiLow > 0);
    }

    [Fact]
    public void Bootstrap_IsDeterministicWithSeed()
    {
        var a = Enumerable.Range(1, 100).Select(i => (double)i).ToArray();
        var b = Enumerable.Range(50, 100).Select(i => (double)i).ToArray();
        var r1 = SignificanceTest.BootstrapMedianDiff(a, b, iterations: 500, seed: 7);
        var r2 = SignificanceTest.BootstrapMedianDiff(a, b, iterations: 500, seed: 7);
        Assert.Equal(r1.MedianDelta, r2.MedianDelta);
        Assert.Equal(r1.CiLow, r2.CiLow);
        Assert.Equal(r1.CiHigh, r2.CiHigh);
    }

    private static double SampleNormal(Random rng, double mean, double stddev)
    {
        double u1, u2;
        do { u1 = rng.NextDouble(); } while (u1 <= double.Epsilon);
        u2 = rng.NextDouble();
        var z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        return mean + z * stddev;
    }
}
