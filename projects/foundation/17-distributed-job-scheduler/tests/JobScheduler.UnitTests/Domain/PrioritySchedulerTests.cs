using JobScheduler.Domain;

namespace JobScheduler.UnitTests.Domain;

/// <summary>Priority ordering with aging so low-priority work cannot starve indefinitely.</summary>
public sealed class PrioritySchedulerTests
{
    private sealed record Candidate(int Priority, DateTimeOffset ScheduledAt);

    private static readonly DateTimeOffset Now = new(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EffectivePriority_without_aging_is_the_base()
    {
        Assert.Equal(10, PriorityScheduler.EffectivePriority(10, TimeSpan.FromHours(5), agingBoostPerMinute: 0));
        Assert.Equal(10, PriorityScheduler.EffectivePriority(10, TimeSpan.Zero, agingBoostPerMinute: 1));
    }

    [Fact]
    public void EffectivePriority_grows_with_waiting_time()
    {
        // 100 minutes waiting at 1 boost/min adds 100.
        Assert.Equal(101, PriorityScheduler.EffectivePriority(1, TimeSpan.FromMinutes(100), agingBoostPerMinute: 1));
    }

    [Fact]
    public void Aging_lets_an_old_low_priority_job_overtake_a_fresh_high_priority_one()
    {
        var high = new Candidate(10, Now);                          // just scheduled
        var lowButOld = new Candidate(1, Now - TimeSpan.FromMinutes(100)); // waited 100 min

        var ordered = PriorityScheduler.Order(
            [high, lowButOld], c => c.Priority, c => c.ScheduledAt, Now, agingBoostPerMinute: 1);

        Assert.Equal(lowButOld, ordered[0]); // eff 101 > 10 -> no starvation
        Assert.Equal(high, ordered[1]);
    }

    [Fact]
    public void Strict_priority_when_aging_disabled()
    {
        var high = new Candidate(10, Now);
        var lowButOld = new Candidate(1, Now - TimeSpan.FromDays(1));

        var ordered = PriorityScheduler.Order(
            [high, lowButOld], c => c.Priority, c => c.ScheduledAt, Now, agingBoostPerMinute: 0);

        Assert.Equal(high, ordered[0]);
    }

    [Fact]
    public void Ties_break_by_oldest_scheduled_time()
    {
        var older = new Candidate(5, Now - TimeSpan.FromMinutes(1));
        var newer = new Candidate(5, Now);

        var ordered = PriorityScheduler.Order(
            [newer, older], c => c.Priority, c => c.ScheduledAt, Now, agingBoostPerMinute: 0);

        Assert.Equal(older, ordered[0]);
    }
}
