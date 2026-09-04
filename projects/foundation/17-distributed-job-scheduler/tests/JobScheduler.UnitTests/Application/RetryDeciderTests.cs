using JobScheduler.Application.Services;
using JobScheduler.Domain;

namespace JobScheduler.UnitTests.Application;

/// <summary>Retry-vs-dead-letter decisioning including poison detection.</summary>
public sealed class RetryDeciderTests
{
    private static readonly DateTimeOffset Now = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static RetryPolicy Policy(int max = 3) =>
        new(RetryStrategy.Exponential, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1000), max);

    [Fact]
    public void Retries_while_attempts_remain_scheduling_the_backoff()
    {
        var decision = RetryDecider.Decide(completedAttempts: 1, Policy(), Now, retriable: true);
        Assert.True(decision.ShouldRetry);
        Assert.Equal(Now.AddSeconds(2), decision.NextScheduledAt); // 2 * 2^0
    }

    [Fact]
    public void DeadLetters_when_the_attempt_budget_is_exhausted()
    {
        var decision = RetryDecider.Decide(completedAttempts: 3, Policy(max: 3), Now, retriable: true);
        Assert.False(decision.ShouldRetry);
        Assert.Null(decision.NextScheduledAt);
        Assert.Contains("poison", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeadLetters_immediately_on_a_non_retriable_failure()
    {
        var decision = RetryDecider.Decide(completedAttempts: 1, Policy(), Now, retriable: false);
        Assert.False(decision.ShouldRetry);
        Assert.Contains("non-retriable", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Retry_delay_can_be_pinned_with_a_jitter_sample()
    {
        var policy = new RetryPolicy(RetryStrategy.ExponentialJitter, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1000), 3, 0.2);
        var decision = RetryDecider.Decide(1, policy, Now, retriable: true, jitterSample: 0.0);
        Assert.Equal(Now.AddSeconds(1.6), decision.NextScheduledAt); // 0.8 factor
    }
}
