namespace Sagas.Core;

public enum Phase
{
    Forward,
    Compensating,

    /// <summary>Terminal: every step committed.</summary>
    Done,

    /// <summary>Terminal: rolled back. Whether that rollback was <i>clean</i> is a separate question.</summary>
    Aborted,

    /// <summary>
    /// Terminal, and the reason this tool exists. The saga can neither finish nor
    /// roll back. In production this is the 3am page that ends with somebody
    /// editing rows by hand.
    /// </summary>
    Stuck
}

/// <summary>
/// One node of the model. Six fields, all value-comparable.
///
/// <see cref="Landed"/> packs, two bits per step, how many times that step's
/// effect is currently present in the world. It is deliberately *not* what the
/// orchestrator knows -- the orchestrator's whole problem is that it cannot see
/// this. It exists so the checker can ask the one question that needs ground
/// truth: "is this compensation about to undo something that never happened?"
///
/// It began as one bit per step and that was wrong. A non-idempotent step can
/// land twice; after one compensation the bit read "not present" while the effect
/// was still there once over, and the checker reported a neutrality violation
/// against a compensation that was behaving correctly. Two bits, saturating at
/// three, costs nothing measurable and removes the whole class of false report.
/// </summary>
public readonly record struct Config(
    WorldState State,
    Phase Phase,
    int Cursor,
    int Attempts,
    int Crashes,
    int Landed)
{
    private const int BitsPerStep = 2;
    private const int CounterMask = 3;

    /// <summary>Fifteen steps at two bits each fits in an int with a bit to spare.</summary>
    public const int MaxSteps = 15;

    public int LandedCount(int step) => (Landed >> (step * BitsPerStep)) & CounterMask;

    public int Landing(int step)
    {
        var current = LandedCount(step);
        return current >= CounterMask ? Landed : Landed + (1 << (step * BitsPerStep));
    }

    public int Undoing(int step)
    {
        var current = LandedCount(step);
        return current == 0 ? Landed : Landed - (1 << (step * BitsPerStep));
    }

    public bool IsTerminal => Phase is Phase.Done or Phase.Aborted or Phase.Stuck;

    public override string ToString() =>
        Phase switch
        {
            Phase.Done => $"DONE {State}",
            Phase.Aborted => $"ABORTED {State}",
            Phase.Stuck => $"STUCK {State}",
            _ => $"{Phase}@{Cursor} try{Attempts} crash{Crashes} {State}"
        };
}

/// <summary>What the environment did to a step invocation.</summary>
public enum Outcome
{
    /// <summary>Effect applied, acknowledgement received.</summary>
    Ok,

    /// <summary>
    /// Effect applied, acknowledgement lost. The orchestrator sees a timeout and
    /// cannot distinguish this from <see cref="NotApplied"/>. Almost every saga
    /// bug in this project's report lives in that inability.
    /// </summary>
    LostAck,

    /// <summary>Effect not applied, and the participant said so definitively.</summary>
    Rejected,

    /// <summary>Effect not applied; indistinguishable to the orchestrator from <see cref="LostAck"/>.</summary>
    NotApplied,

    /// <summary>The orchestrator died and recovered from its journal.</summary>
    Crash
}

/// <summary>Where rollback starts after the orchestrator gives up on step <i>i</i>.</summary>
public enum CompensationScope
{
    /// <summary>
    /// Compensate step <i>i</i> as well, because a timeout does not prove the
    /// effect did not land. Requires every compensation to be safe to run for a
    /// step that never executed.
    /// </summary>
    IncludeUncertainStep,

    /// <summary>
    /// Start at <i>i-1</i>, assuming that a step which did not acknowledge did not
    /// happen. This is the intuitive choice and section 3 of the report is about
    /// why it is wrong.
    /// </summary>
    AssumeUncertainStepDidNotRun
}

public sealed record CheckerOptions
{
    /// <summary>
    /// How many orchestrator crashes an execution may contain. Zero explores only
    /// participant failures. The report checks each result at successive budgets
    /// and states which budget first exposed each violation, because "no
    /// violation at budget 1" and "no violations" are different claims.
    /// </summary>
    public int CrashBudget { get; init; } = 1;

    public CompensationScope Scope { get; init; } = CompensationScope.IncludeUncertainStep;

    /// <summary>
    /// Whether the retry counter survives a crash. If the orchestrator keeps
    /// attempt counts in memory, a crash resets them and the retry cap stops
    /// being a cap.
    /// </summary>
    public bool JournalAttempts { get; init; } = true;

    /// <summary>
    /// Model the obligation that a compensation is retried until it succeeds.
    /// When true, the checker generates only the succeeding transition once the
    /// attempt cap is reached -- a weak-fairness assumption, not a guarantee that
    /// nothing goes wrong. When false, exhausting compensation retries is
    /// <see cref="Phase.Stuck"/>.
    /// </summary>
    public bool CompensationRetriesForever { get; init; } = true;

    public int CompensationAttempts { get; init; } = 2;

    /// <summary>Safety valve. A run that hits this is reported as incomplete, never as passing.</summary>
    public int MaxStates { get; init; } = 4_000_000;
}
