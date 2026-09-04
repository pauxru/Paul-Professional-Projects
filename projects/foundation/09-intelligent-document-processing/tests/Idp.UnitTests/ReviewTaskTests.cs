using Idp.Application.Pipeline;
using Idp.Domain.Review;

namespace Idp.UnitTests;

/// <summary>Review task claim/lock, expiry takeover, concurrency conflict, SLA aging and completion.</summary>
public class ReviewTaskTests
{
    private static readonly DateTime Now = new(2024, 6, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Sla = TimeSpan.FromHours(24);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(30);

    private static ReviewTask New(double confidence = 0.6, decimal? value = 1000m) =>
        new(Guid.NewGuid(), ReviewPriority.Calculate(value, confidence), value, confidence, Now, Sla);

    [Fact]
    public void Claim_locks_the_task_for_the_reviewer()
    {
        var task = New();
        task.Claim("alice", Now, Lease);
        Assert.Equal(ReviewStatus.Claimed, task.Status);
        Assert.Equal("alice", task.ClaimedBy);
        Assert.True(task.IsClaimActive(Now));
    }

    [Fact]
    public void Concurrent_claim_by_another_reviewer_conflicts()
    {
        var task = New();
        task.Claim("alice", Now, Lease);
        Assert.Throws<ReviewClaimConflictException>(() => task.Claim("bob", Now.AddMinutes(1), Lease));
    }

    [Fact]
    public void Same_reviewer_may_reclaim()
    {
        var task = New();
        task.Claim("alice", Now, Lease);
        var ex = Record.Exception(() => task.Claim("alice", Now.AddMinutes(5), Lease));
        Assert.Null(ex);
    }

    [Fact]
    public void Expired_claim_can_be_taken_over()
    {
        var task = New();
        task.Claim("alice", Now, Lease);
        var afterExpiry = Now + Lease + TimeSpan.FromMinutes(1);
        Assert.False(task.IsClaimActive(afterExpiry));
        Assert.True(task.IsClaimable(afterExpiry));
        var ex = Record.Exception(() => task.Claim("bob", afterExpiry, Lease));
        Assert.Null(ex);
        Assert.Equal("bob", task.ClaimedBy);
    }

    [Fact]
    public void Completed_task_cannot_be_claimed()
    {
        var task = New();
        task.Claim("alice", Now, Lease);
        task.Complete(ReviewResolution.Approved, "alice", Now.AddMinutes(10));
        Assert.Equal(ReviewStatus.Completed, task.Status);
        Assert.Throws<InvalidOperationException>(() => task.Claim("bob", Now.AddMinutes(11), Lease));
    }

    [Fact]
    public void Task_becomes_overdue_after_the_sla_window()
    {
        var task = New();
        Assert.False(task.IsOverdue(Now.AddHours(1)));
        Assert.True(task.IsOverdue(Now + Sla + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Priority_rewards_high_value_and_low_confidence()
    {
        var highValueLowConf = ReviewPriority.Calculate(50_000m, 0.20);
        var lowValueHighConf = ReviewPriority.Calculate(100m, 0.95);
        Assert.True(highValueLowConf > lowValueHighConf);
    }
}
