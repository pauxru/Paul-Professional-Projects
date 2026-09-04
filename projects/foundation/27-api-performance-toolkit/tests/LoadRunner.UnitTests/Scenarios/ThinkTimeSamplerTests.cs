using LoadRunner.Core.Scenarios;
using Xunit;

namespace LoadRunner.UnitTests.Scenarios;

public class ThinkTimeSamplerTests
{
    [Fact]
    public void ConstantDistribution_ReturnsMeanEveryTime()
    {
        var sampler = new ThinkTimeSampler(seed: 1);
        var spec = new ThinkTimeSpec(ThinkTimeDistribution.Constant, Mean: 0.25);
        for (var i = 0; i < 20; i++)
            Assert.Equal(TimeSpan.FromSeconds(0.25), sampler.Sample(spec));
    }

    [Fact]
    public void UniformDistribution_StaysWithinBounds()
    {
        var sampler = new ThinkTimeSampler(seed: 2);
        var spec = new ThinkTimeSpec(ThinkTimeDistribution.Uniform, Min: 0.1, Max: 0.5);
        for (var i = 0; i < 200; i++)
        {
            var d = sampler.Sample(spec).TotalSeconds;
            Assert.InRange(d, 0.1, 0.5);
        }
    }

    [Fact]
    public void NormalDistribution_HasApproximateMean()
    {
        var sampler = new ThinkTimeSampler(seed: 3);
        var spec = new ThinkTimeSpec(ThinkTimeDistribution.Normal, Mean: 0.20, StdDev: 0.05);
        double total = 0;
        var n = 5000;
        for (var i = 0; i < n; i++) total += sampler.Sample(spec).TotalSeconds;
        var mean = total / n;
        Assert.InRange(mean, 0.17, 0.23);
    }

    [Fact]
    public void ExponentialDistribution_IsNonNegative_AndMeanCloseToLambda()
    {
        var sampler = new ThinkTimeSampler(seed: 4);
        var spec = new ThinkTimeSpec(ThinkTimeDistribution.Exponential, Mean: 0.2);
        double total = 0;
        var n = 5000;
        for (var i = 0; i < n; i++)
        {
            var d = sampler.Sample(spec).TotalSeconds;
            Assert.True(d >= 0);
            total += d;
        }
        var mean = total / n;
        Assert.InRange(mean, 0.15, 0.25);
    }

    [Fact]
    public void NoneDistribution_ReturnsZero()
    {
        var sampler = new ThinkTimeSampler(seed: 5);
        var spec = new ThinkTimeSpec(ThinkTimeDistribution.None);
        Assert.Equal(TimeSpan.Zero, sampler.Sample(spec));
    }
}
