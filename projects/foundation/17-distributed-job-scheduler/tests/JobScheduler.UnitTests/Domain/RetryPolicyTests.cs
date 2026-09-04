using JobScheduler.Domain;

namespace JobScheduler.UnitTests.Domain;

/// <summary>Backoff maths: fixed, exponential, exponential-with-jitter and the delay cap.</summary>
public sealed class RetryPolicyTests
{
    private static RetryPolicy Policy(RetryStrategy strategy, double baseSec = 2, double maxSec = 1000, int max = 3, double jitter = 0.2)
        => new(strategy, TimeSpan.FromSeconds(baseSec), TimeSpan.FromSeconds(maxSec), max, jitter);

    [Fact]
    public void Fixed_delay_is_constant()
    {
        var p = Policy(RetryStrategy.Fixed);
        Assert.Equal(TimeSpan.FromSeconds(2), p.NextDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(2), p.NextDelay(5));
    }

    [Fact]
    public void Exponential_doubles_each_attempt()
    {
        var p = Policy(RetryStrategy.Exponential);
        Assert.Equal(TimeSpan.FromSeconds(2), p.NextDelay(1));  // 2 * 2^0
        Assert.Equal(TimeSpan.FromSeconds(4), p.NextDelay(2));  // 2 * 2^1
        Assert.Equal(TimeSpan.FromSeconds(8), p.NextDelay(3));  // 2 * 2^2
    }

    [Fact]
    public void Exponential_is_capped_at_max_delay()
    {
        var p = Policy(RetryStrategy.Exponential, baseSec: 10, maxSec: 15);
        Assert.Equal(TimeSpan.FromSeconds(10), p.NextDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(15), p.NextDelay(2)); // 20 capped to 15
        Assert.Equal(TimeSpan.FromSeconds(15), p.NextDelay(9));
    }

    [Theory]
    [InlineData(0.0, 1.6)]   // 1 - 0.2 = 0.8 factor over 2s
    [InlineData(0.5, 2.0)]   // centre of band
    [InlineData(1.0, 2.4)]   // 1 + 0.2 = 1.2 factor
    public void ExponentialJitter_samples_within_the_band(double sample, double expectedSeconds)
    {
        var p = Policy(RetryStrategy.ExponentialJitter, baseSec: 2, jitter: 0.2);
        var delay = p.NextDelay(1, sample);
        Assert.Equal(expectedSeconds, delay.TotalSeconds, precision: 6);
    }

    [Fact]
    public void ShouldRetry_is_true_until_attempt_budget_exhausted()
    {
        var p = Policy(RetryStrategy.Fixed, max: 3);
        Assert.True(p.ShouldRetry(0));
        Assert.True(p.ShouldRetry(2));
        Assert.False(p.ShouldRetry(3));
        Assert.False(p.ShouldRetry(4));
    }

    [Theory]
    [InlineData(-1, 10, 3)]   // negative base
    [InlineData(10, 5, 3)]    // max < base
    [InlineData(1, 10, 0)]    // zero attempts
    public void Constructor_rejects_invalid_configuration(double baseSec, double maxSec, int max)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RetryPolicy(RetryStrategy.Fixed, TimeSpan.FromSeconds(baseSec), TimeSpan.FromSeconds(maxSec), max));
    }

    [Fact]
    public void NextDelay_rejects_nonpositive_attempt()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy(RetryStrategy.Fixed).NextDelay(0));
    }
}
