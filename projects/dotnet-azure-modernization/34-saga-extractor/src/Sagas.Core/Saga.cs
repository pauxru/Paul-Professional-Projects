namespace Sagas.Core;

/// <summary>
/// The Garcia-Molina / Helland classification. Getting this vocabulary right is
/// most of the design work, because the well-formedness rule falls straight out
/// of it: <b>compensatable steps, then one pivot, then retriable steps</b>. A
/// saga that does not have that shape has a reachable state it cannot leave, and
/// <see cref="Checker"/> will produce the trace that reaches it.
/// </summary>
public enum StepKind
{
    /// <summary>Has a semantic inverse. May run before the pivot.</summary>
    Compensatable,

    /// <summary>
    /// The point of no return. Its effect is externally visible and cannot be
    /// withdrawn: the email is sent, the goods have left the building, the
    /// regulator has been notified. Once it commits the saga must roll forward.
    /// </summary>
    Pivot,

    /// <summary>
    /// Has no inverse either, but is guaranteed to succeed eventually, so it is
    /// safe after the pivot. "Guaranteed" is a real obligation on the
    /// participant, not a wish -- see <see cref="Step.CanBeRejected"/>.
    /// </summary>
    Retriable
}

public delegate WorldState Effect(WorldState state);

/// <summary>A named predicate over a state, used for invariants and guards.</summary>
public sealed record Invariant(string Name, Func<WorldState, bool> Holds);

public sealed record Step
{
    public required string Name { get; init; }

    /// <summary>The service that owns the data this step writes.</summary>
    public required string Service { get; init; }

    public required StepKind Kind { get; init; }

    public required Effect Forward { get; init; }

    /// <summary>
    /// Null for <see cref="StepKind.Pivot"/> and <see cref="StepKind.Retriable"/>.
    /// </summary>
    public Effect? Compensate { get; init; }

    /// <summary>
    /// Claim that applying <see cref="Forward"/> twice is indistinguishable from
    /// applying it once. This is a claim the checker <i>verifies</i> against every
    /// reachable state rather than trusting: see
    /// <see cref="PropertyKind.DeclaredIdempotenceIsFalse"/>. Declaring it and
    /// being wrong is worse than not declaring it, because the retry policy is
    /// derived from the declaration.
    /// </summary>
    public bool Idempotent { get; init; }

    /// <summary>As <see cref="Idempotent"/>, for the compensation.</summary>
    public bool CompensationIdempotent { get; init; }

    /// <summary>
    /// Whether the participant can be asked, after a timeout, what actually
    /// happened -- an idempotency-key status lookup, a transaction-id query.
    ///
    /// This is the single most valuable property a step can have and it is the one
    /// nobody puts in the design document. Without it a timeout is permanently
    /// ambiguous, and an ambiguous timeout on the pivot is unrecoverable by
    /// construction: the orchestrator cannot roll forward because the step may not
    /// have happened, and cannot roll back because it may have. Section 5 of the
    /// report is that counterexample.
    /// </summary>
    public bool Queryable { get; init; }

    /// <summary>
    /// Whether the participant can answer with a definitive "no". A step that can
    /// only ever time out is a different risk profile from one that can refuse:
    /// timeouts are ambiguous, refusals are not.
    /// </summary>
    public bool CanBeRejected { get; init; } = true;

    /// <summary>
    /// Whether the <i>compensation</i> can fail definitively. This is the flag
    /// that most designs quietly assume is false. Section 4 of the report is
    /// about what happens when it is not.
    /// </summary>
    public bool CompensationCanBeRejected { get; init; }

    /// <summary>
    /// What happens when the compensation is definitively refused: the hold
    /// lapses on its own, the reservation times out, the manual queue picks it up.
    ///
    /// Supplying this is not a way to make a warning go away. It converts an
    /// unrecoverable state into a terminal state with residue, and that residue
    /// then has to be declared in <see cref="Saga.AcceptableResidue"/> -- which
    /// means someone has written down, in the model, what the business is willing
    /// to be left holding. That is the actual deliverable.
    /// </summary>
    public Effect? CompensationFallback { get; init; }

    /// <summary>
    /// How many times the orchestrator will retry before giving up on this step.
    /// Bounds the state space; <see cref="Checker"/> reports whether the bound
    /// was ever reached, so that "no violation found" can be distinguished from
    /// "not explored far enough".
    /// </summary>
    public int MaxAttempts { get; init; } = 2;

    public override string ToString() => $"{Name}@{Service} [{Kind}]";
}

public sealed class Saga
{
    public Saga(string name, Schema schema, WorldState initial, IEnumerable<Step> steps)
    {
        Name = name;
        Schema = schema;
        Initial = initial;
        Steps = steps.ToArray();
        if (Steps.Count == 0)
        {
            throw new ArgumentException("a saga needs at least one step", nameof(steps));
        }

        if (Steps.Count > Config.MaxSteps)
        {
            throw new ArgumentException(
                $"{Steps.Count} steps exceeds the {Config.MaxSteps} that fit in the packed landing counter; " +
                "raise Config.MaxSteps and widen the counter, or decompose the saga",
                nameof(steps));
        }

        foreach (var s in Steps)
        {
            var needsCompensation = s.Kind == StepKind.Compensatable;
            if (needsCompensation && s.Compensate is null)
            {
                throw new ArgumentException($"step '{s.Name}' is compensatable but has no compensation");
            }

            if (!needsCompensation && s.Compensate is not null)
            {
                throw new ArgumentException(
                    $"step '{s.Name}' is {s.Kind} but supplies a compensation; a {s.Kind} step must never be rolled back");
            }
        }
    }

    public string Name { get; }

    public Schema Schema { get; }

    public WorldState Initial { get; }

    public IReadOnlyList<Step> Steps { get; }

    public List<Invariant> Invariants { get; } = [];

    /// <summary>
    /// Accepted differences between the initial state and the state after a
    /// fully compensated abort. Empty means "perfect rollback demanded". A
    /// non-empty value is a design decision that ought to be written down, which
    /// is why it lives here rather than being hard-coded into the checker.
    /// </summary>
    public HashSet<string> AcceptableResidue { get; } = new(StringComparer.Ordinal);

    public Saga Invariant(string name, Func<WorldState, bool> holds)
    {
        Invariants.Add(new Invariant(name, holds));
        return this;
    }

    public Saga AllowResidue(params string[] variables)
    {
        foreach (var v in variables)
        {
            _ = Schema.IndexOf(v);
            AcceptableResidue.Add(v);
        }

        return this;
    }

    public int PivotIndex
    {
        get
        {
            for (var i = 0; i < Steps.Count; i++)
            {
                if (Steps[i].Kind == StepKind.Pivot)
                {
                    return i;
                }
            }

            return -1;
        }
    }

    /// <summary>
    /// The structural rule, checked without exploring anything: compensatable
    /// steps, then at most one pivot, then retriable steps. Returns the reasons
    /// it is violated, empty when the shape is right.
    ///
    /// This is deliberately separate from <see cref="Checker"/>. A shape error is
    /// a fact about the design; a reachability violation is a fact about an
    /// execution. Reporting them through the same channel would let a reader
    /// think a well-shaped saga had been proved safe when it had only been
    /// proved well-shaped.
    /// </summary>
    public IReadOnlyList<string> ShapeViolations()
    {
        var problems = new List<string>();
        var pivots = Steps.Where(s => s.Kind == StepKind.Pivot).ToList();
        if (pivots.Count > 1)
        {
            problems.Add(
                $"{pivots.Count} pivots ({string.Join(", ", pivots.Select(p => p.Name))}); " +
                "a saga has at most one point of no return, because two of them cannot both be the last reversible moment");
        }

        var pivot = PivotIndex;
        var boundary = pivot >= 0 ? pivot : Steps.Count;

        for (var i = 0; i < boundary; i++)
        {
            if (Steps[i].Kind == StepKind.Retriable)
            {
                problems.Add(
                    $"step {i} '{Steps[i].Name}' is retriable but sits before the pivot; " +
                    "if a later compensatable step aborts, this one cannot be undone");
            }
        }

        for (var i = boundary + 1; i < Steps.Count; i++)
        {
            if (Steps[i].Kind == StepKind.Compensatable)
            {
                problems.Add(
                    $"step {i} '{Steps[i].Name}' is compensatable but sits after the pivot; " +
                    "it can never actually be compensated, because rolling back past the pivot is impossible");
            }
        }

        for (var i = 0; i < Steps.Count; i++)
        {
            if (Steps[i].Kind == StepKind.Retriable && Steps[i].CanBeRejected)
            {
                problems.Add(
                    $"step {i} '{Steps[i].Name}' is declared retriable but can be definitively rejected; " +
                    "'retriable' is a guarantee that retrying eventually works, and a hard rejection breaks it");
            }
        }

        return problems;
    }

    public override string ToString() => $"{Name} ({Steps.Count} steps)";
}
