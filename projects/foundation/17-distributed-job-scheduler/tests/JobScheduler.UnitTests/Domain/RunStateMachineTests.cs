using JobScheduler.Domain;

namespace JobScheduler.UnitTests.Domain;

/// <summary>The run lifecycle transition table is the single source of truth for legal moves.</summary>
public sealed class RunStateMachineTests
{
    [Theory]
    [InlineData(RunState.Pending, RunState.Claimed)]
    [InlineData(RunState.Pending, RunState.Cancelled)]
    [InlineData(RunState.Claimed, RunState.Running)]
    [InlineData(RunState.Claimed, RunState.Pending)]      // reclaim
    [InlineData(RunState.Running, RunState.Succeeded)]
    [InlineData(RunState.Running, RunState.Failed)]
    [InlineData(RunState.Running, RunState.TimedOut)]
    [InlineData(RunState.Running, RunState.Cancelled)]
    [InlineData(RunState.Failed, RunState.Retrying)]
    [InlineData(RunState.Failed, RunState.DeadLettered)]
    [InlineData(RunState.TimedOut, RunState.Retrying)]
    [InlineData(RunState.Retrying, RunState.Pending)]
    [InlineData(RunState.DeadLettered, RunState.Pending)] // replay
    public void Legal_transitions_are_allowed(RunState from, RunState to)
    {
        Assert.True(RunStateMachine.CanTransition(from, to));
    }

    [Theory]
    [InlineData(RunState.Pending, RunState.Running)]
    [InlineData(RunState.Pending, RunState.Succeeded)]
    [InlineData(RunState.Succeeded, RunState.Running)]
    [InlineData(RunState.Cancelled, RunState.Pending)]
    [InlineData(RunState.Running, RunState.Claimed)]
    public void Illegal_transitions_are_rejected(RunState from, RunState to)
    {
        Assert.False(RunStateMachine.CanTransition(from, to));
    }

    [Fact]
    public void EnsureTransition_throws_on_illegal_move()
    {
        var ex = Assert.Throws<InvalidStateTransitionException>(
            () => RunStateMachine.EnsureTransition(RunState.Pending, RunState.Succeeded));
        Assert.Equal(RunState.Pending, ex.From);
        Assert.Equal(RunState.Succeeded, ex.To);
    }

    [Theory]
    [InlineData(RunState.Succeeded, true)]
    [InlineData(RunState.Cancelled, true)]
    [InlineData(RunState.DeadLettered, true)]
    [InlineData(RunState.Failed, false)]
    [InlineData(RunState.Running, false)]
    public void IsTerminal_identifies_end_states(RunState state, bool expected)
    {
        Assert.Equal(expected, RunStateMachine.IsTerminal(state));
    }

    [Theory]
    [InlineData(RunState.Claimed, true)]
    [InlineData(RunState.Running, true)]
    [InlineData(RunState.Pending, false)]
    [InlineData(RunState.Succeeded, false)]
    public void IsActive_identifies_slot_occupying_states(RunState state, bool expected)
    {
        Assert.Equal(expected, RunStateMachine.IsActive(state));
    }
}
