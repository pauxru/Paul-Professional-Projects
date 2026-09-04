using JobScheduler.Domain;
using JobScheduler.Domain.Entities;

namespace JobScheduler.UnitTests.Domain;

/// <summary>
/// Entity-level fencing and lifecycle guarantees on <see cref="JobRun"/>: monotonic fencing tokens,
/// fencing-guarded terminal writes, lease heartbeat ownership, and reclaim.
/// </summary>
public sealed class JobRunFencingTests
{
    private static readonly DateTimeOffset T0 = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static JobRun NewRun()
    {
        var def = JobDefinition.Create("nightly-etl", "csv-transform", T0);
        return JobRun.Create(def, T0, T0, "idem-1", "corr-1", "manual");
    }

    [Fact]
    public void Create_starts_pending_with_zero_attempts_and_token()
    {
        var run = NewRun();
        Assert.Equal(RunState.Pending, run.State);
        Assert.Equal(0, run.AttemptCount);
        Assert.Equal(0, run.FencingToken);
    }

    [Fact]
    public void Claim_requires_a_strictly_greater_fencing_token()
    {
        var run = NewRun();
        run.Claim("node-1", Guid.NewGuid(), fencingToken: 1, leaseExpiresAt: T0.AddSeconds(30));
        Assert.Equal(RunState.Claimed, run.State);
        Assert.Equal(1, run.FencingToken);
        Assert.Equal("node-1", run.LeaseOwner);

        // Re-claiming with a non-increasing token is rejected (monotonicity).
        var run2 = NewRun();
        Assert.Throws<InvalidOperationException>(
            () => run2.Claim("node-1", Guid.NewGuid(), fencingToken: 0, leaseExpiresAt: T0));
    }

    [Fact]
    public void BeginRunning_increments_the_attempt_counter()
    {
        var run = NewRun();
        run.Claim("node-1", Guid.NewGuid(), 1, T0.AddSeconds(30));
        run.BeginRunning(T0.AddSeconds(1));
        Assert.Equal(RunState.Running, run.State);
        Assert.Equal(1, run.AttemptCount);
    }

    [Fact]
    public void Succeed_with_the_wrong_fencing_token_is_rejected()
    {
        var run = NewRun();
        run.Claim("node-1", Guid.NewGuid(), 1, T0.AddSeconds(30));
        run.BeginRunning(T0.AddSeconds(1));

        var ex = Assert.Throws<FencingTokenException>(() => run.Succeed(fencingToken: 2, "out", T0.AddSeconds(2)));
        Assert.Equal(2, ex.Presented);
        Assert.Equal(1, ex.Current);
        Assert.Equal(RunState.Running, run.State); // unchanged
    }

    [Fact]
    public void Succeed_with_the_matching_token_completes_and_clears_the_lease()
    {
        var run = NewRun();
        var token = Guid.NewGuid();
        run.Claim("node-1", token, 1, T0.AddSeconds(30));
        run.BeginRunning(T0.AddSeconds(1));
        run.Succeed(1, "output", T0.AddSeconds(2));

        Assert.Equal(RunState.Succeeded, run.State);
        Assert.Equal("output", run.Output);
        Assert.Null(run.LeaseOwner);
        Assert.Null(run.LeaseToken);
    }

    [Fact]
    public void Heartbeat_only_succeeds_for_the_lease_owner_token()
    {
        var run = NewRun();
        var token = Guid.NewGuid();
        run.Claim("node-1", token, 1, T0.AddSeconds(30));
        run.BeginRunning(T0.AddSeconds(1));

        Assert.False(run.Heartbeat(Guid.NewGuid(), T0.AddSeconds(60))); // wrong token
        Assert.True(run.Heartbeat(token, T0.AddSeconds(60)));           // owner extends
        Assert.Equal(T0.AddSeconds(60), run.LeaseExpiresAt);
    }

    [Fact]
    public void ReclaimExpired_returns_a_running_run_to_pending()
    {
        var run = NewRun();
        run.Claim("node-1", Guid.NewGuid(), 1, T0.AddSeconds(30));
        run.BeginRunning(T0.AddSeconds(1));

        Assert.True(run.IsLeaseExpired(T0.AddSeconds(31)));
        run.ReclaimExpired(T0.AddSeconds(31));

        Assert.Equal(RunState.Pending, run.State);
        Assert.Null(run.LeaseOwner);
        // Fencing token is NOT bumped by reclaim; the next claim bumps it, fencing the old worker.
        Assert.Equal(1, run.FencingToken);
    }

    [Fact]
    public void Retry_rearms_a_failed_run_as_pending()
    {
        var run = NewRun();
        run.Claim("node-1", Guid.NewGuid(), 1, T0.AddSeconds(30));
        run.BeginRunning(T0.AddSeconds(1));
        run.Fail(1, "boom", T0.AddSeconds(2));

        run.Retry(T0.AddSeconds(10));
        Assert.Equal(RunState.Pending, run.State);
        Assert.Equal(T0.AddSeconds(10), run.ScheduledAt);
        Assert.Null(run.Error);
    }

    [Fact]
    public void Cancel_from_pending_is_terminal()
    {
        var run = NewRun();
        run.Cancel(T0.AddSeconds(1));
        Assert.Equal(RunState.Cancelled, run.State);
        Assert.True(RunStateMachine.IsTerminal(run.State));
    }

    [Fact]
    public void ReplayReset_from_dead_letter_starts_a_fresh_attempt()
    {
        var run = NewRun();
        run.Claim("node-1", Guid.NewGuid(), 1, T0.AddSeconds(30));
        run.BeginRunning(T0.AddSeconds(1));
        run.Fail(1, "boom", T0.AddSeconds(2));
        run.DeadLetter();
        Assert.Equal(RunState.DeadLettered, run.State);

        run.ReplayReset(T0.AddSeconds(100));
        Assert.Equal(RunState.Pending, run.State);
        Assert.Equal(0, run.AttemptCount);
    }
}
