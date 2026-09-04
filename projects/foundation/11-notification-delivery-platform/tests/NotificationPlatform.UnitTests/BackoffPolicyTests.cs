namespace NotificationPlatform.UnitTests;

using NotificationPlatform.Application.Fairness;
using Xunit;

public sealed class BackoffPolicyTests
{
    [Fact]
    public void GrowsExponentiallyAndRespectsCap()
    {
        var p = new ExponentialBackoffPolicy(baseMs: 100, maxMs: 5000, seed: 42);
        var delays = Enumerable.Range(1, 8).Select(i => p.NextDelay(i).TotalMilliseconds).ToArray();
        // Cap is 5000, half is 2500, jitter up to +2500 => max ~5000ms.
        Assert.All(delays, d => Assert.InRange(d, 50, 5000));
        // First delay ~50-100 ms; late delays should be much bigger.
        Assert.True(delays[6] > delays[0], $"expected late >>early, got early={delays[0]}, late={delays[6]}");
    }

    [Fact]
    public void DeterministicWithSameSeed()
    {
        var a = new ExponentialBackoffPolicy(100, 5000, seed: 7);
        var b = new ExponentialBackoffPolicy(100, 5000, seed: 7);
        for (int i = 1; i < 6; i++)
            Assert.Equal(a.NextDelay(i), b.NextDelay(i));
    }

    [Fact]
    public void AttemptZeroTreatedAsOne()
    {
        var p = new ExponentialBackoffPolicy(100, 5000, seed: 42);
        var d0 = p.NextDelay(0);
        Assert.True(d0.TotalMilliseconds >= 50);
    }
}
