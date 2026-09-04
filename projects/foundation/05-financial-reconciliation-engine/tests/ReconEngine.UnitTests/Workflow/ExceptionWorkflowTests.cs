using ReconEngine.Domain.Abstractions;
using ReconEngine.Domain.Entities;
using ReconEngine.Domain.Enums;
using ReconEngine.UnitTests.TestKit;

namespace ReconEngine.UnitTests.Workflow;

/// <summary>The exception manual-resolution state machine and its four-eyes write-off gate.</summary>
public sealed class ExceptionWorkflowTests
{
    private const long Threshold = 100_000;

    private static ReconciliationException New(long amountMinor, ExceptionType type = ExceptionType.AmountMismatch)
    {
        var clock = new TestClock();
        return new ReconciliationException
        {
            ExceptionKey = "K",
            Type = type,
            Severity = ExceptionSeverity.Medium,
            Currency = "KES",
            AmountMinor = amountMinor,
            CreatedAtUtc = clock.UtcNow,
            UpdatedAtUtc = clock.UtcNow,
        };
    }

    [Fact]
    public void Assign_then_resolve_moves_to_resolved()
    {
        var clock = new TestClock();
        var ex = New(10_000);

        ex.Assign("alice", "alice", clock);
        Assert.Equal(ExceptionStatus.Assigned, ex.Status);

        ex.Resolve(ResolutionReasonCode.ManualMatch, "alice", "matched by hand", Threshold, clock);
        Assert.Equal(ExceptionStatus.Resolved, ex.Status);
        Assert.True(ex.IsResolved);
    }

    [Fact]
    public void Small_write_off_resolves_without_approval()
    {
        var clock = new TestClock();
        var ex = New(50_000); // below the 100,000 threshold

        ex.Resolve(ResolutionReasonCode.WriteOff, "alice", null, Threshold, clock);

        Assert.Equal(ExceptionStatus.Resolved, ex.Status);
        Assert.False(ex.ApprovalRequired);
    }

    [Fact]
    public void Large_write_off_requires_four_eyes_approval()
    {
        var clock = new TestClock();
        var ex = New(150_000); // at/above threshold

        ex.Resolve(ResolutionReasonCode.WriteOff, "alice", null, Threshold, clock);

        Assert.Equal(ExceptionStatus.PendingApproval, ex.Status);
        Assert.True(ex.ApprovalRequired);
        Assert.False(ex.IsResolved);
    }

    [Fact]
    public void Proposer_cannot_approve_their_own_write_off()
    {
        var clock = new TestClock();
        var ex = New(150_000);
        ex.Resolve(ResolutionReasonCode.WriteOff, "alice", null, Threshold, clock);

        Assert.Throws<InvalidStateTransitionException>(() => ex.Approve("alice", clock));
        Assert.Equal(ExceptionStatus.PendingApproval, ex.Status);
    }

    [Fact]
    public void Different_user_can_approve_large_write_off()
    {
        var clock = new TestClock();
        var ex = New(150_000);
        ex.Resolve(ResolutionReasonCode.WriteOff, "alice", null, Threshold, clock);

        ex.Approve("bob", clock);

        Assert.Equal(ExceptionStatus.Resolved, ex.Status);
        Assert.Equal("bob", ex.ApprovedBy);
        Assert.Equal("alice", ex.ResolvedBy);
    }

    [Fact]
    public void Rejecting_a_write_off_returns_it_to_the_queue()
    {
        var clock = new TestClock();
        var ex = New(150_000);
        ex.Assign("alice", "alice", clock);
        ex.Resolve(ResolutionReasonCode.WriteOff, "alice", null, Threshold, clock);

        ex.RejectApproval("bob", "insufficient evidence", clock);

        Assert.Equal(ExceptionStatus.Assigned, ex.Status);
        Assert.False(ex.ApprovalRequired);
    }

    [Fact]
    public void Resolved_exception_can_be_reopened_and_reworked()
    {
        var clock = new TestClock();
        var ex = New(10_000);
        ex.Resolve(ResolutionReasonCode.Ignore, "alice", null, Threshold, clock);

        ex.Reopen("carol", "new evidence", clock);
        Assert.Equal(ExceptionStatus.Reopened, ex.Status);

        // A reopened exception is actionable again.
        ex.Resolve(ResolutionReasonCode.ManualMatch, "carol", null, Threshold, clock);
        Assert.Equal(ExceptionStatus.Resolved, ex.Status);
    }

    [Fact]
    public void Cannot_approve_an_exception_that_is_not_pending_approval()
    {
        var clock = new TestClock();
        var ex = New(10_000);

        Assert.Throws<InvalidStateTransitionException>(() => ex.Approve("bob", clock));
    }

    [Fact]
    public void Cannot_reopen_an_unresolved_exception()
    {
        var clock = new TestClock();
        var ex = New(10_000);

        Assert.Throws<InvalidStateTransitionException>(() => ex.Reopen("alice", null, clock));
    }

    [Fact]
    public void Every_transition_appends_an_immutable_audit_entry()
    {
        var clock = new TestClock();
        var ex = New(150_000);

        ex.Assign("alice", "alice", clock);
        ex.Resolve(ResolutionReasonCode.WriteOff, "alice", null, Threshold, clock);
        ex.Approve("bob", clock);

        // assign, resolve->pending, approve->resolved == 3 transitions recorded.
        Assert.Equal(3, ex.AuditTrail.Count);
        Assert.Contains(ex.AuditTrail, a => a.ToStatus == ExceptionStatus.Resolved && a.Actor == "bob");
    }
}
