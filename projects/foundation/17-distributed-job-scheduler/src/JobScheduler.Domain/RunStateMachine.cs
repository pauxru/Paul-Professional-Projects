namespace JobScheduler.Domain;

/// <summary>
/// Encodes the legal transitions of the run lifecycle. This is the single source of
/// truth for what a run is allowed to do next; both the domain entity and the
/// coordination layer consult it so an out-of-order write can never corrupt state.
/// </summary>
public static class RunStateMachine
{
    private static readonly Dictionary<RunState, RunState[]> Allowed = new()
    {
        [RunState.Pending] = [RunState.Claimed, RunState.Cancelled],
        [RunState.Claimed] = [RunState.Running, RunState.Pending, RunState.Cancelled, RunState.TimedOut],
        [RunState.Running] = [RunState.Succeeded, RunState.Failed, RunState.TimedOut, RunState.Cancelled],
        // Failed/TimedOut are evaluated by the retry policy which either re-arms
        // the run (Retrying) or parks it in the dead-letter queue.
        [RunState.Failed] = [RunState.Retrying, RunState.DeadLettered],
        [RunState.TimedOut] = [RunState.Retrying, RunState.DeadLettered],
        // Retrying re-enters the queue as a fresh attempt.
        [RunState.Retrying] = [RunState.Pending, RunState.Cancelled],
        [RunState.Succeeded] = [],
        [RunState.Cancelled] = [],
        [RunState.DeadLettered] = [RunState.Pending] // replay re-arms a dead-lettered run
    };

    public static bool CanTransition(RunState from, RunState to) =>
        Allowed.TryGetValue(from, out var next) && Array.IndexOf(next, to) >= 0;

    public static bool IsTerminal(RunState state) =>
        state is RunState.Succeeded or RunState.Cancelled or RunState.DeadLettered;

    /// <summary>True while a run still occupies a worker/concurrency slot.</summary>
    public static bool IsActive(RunState state) =>
        state is RunState.Claimed or RunState.Running;

    public static void EnsureTransition(RunState from, RunState to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidStateTransitionException(from, to);
        }
    }
}

/// <summary>Raised when code attempts an illegal run-state transition.</summary>
public sealed class InvalidStateTransitionException(RunState from, RunState to)
    : InvalidOperationException($"Illegal run-state transition {from} -> {to}.")
{
    public RunState From { get; } = from;
    public RunState To { get; } = to;
}
