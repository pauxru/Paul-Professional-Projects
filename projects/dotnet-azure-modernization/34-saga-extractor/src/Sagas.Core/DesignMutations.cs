namespace Sagas.Core;

/// <summary>
/// Version 7 with exactly one of its fixes reverted, one saga per fix.
///
/// This is mutation testing applied to a design rather than to code. The claim
/// "we hardened the saga" is worth nothing if three of the six changes were
/// decorative; reverting each one and re-checking says which changes are
/// load-bearing and which were cargo. A revert that produces no violation is not
/// a success, it is a change that should not have been made -- or a hole in the
/// property set.
/// </summary>
public static class DesignMutations
{
    public static IReadOnlyList<(string Label, Saga Saga)> All() =>
    [
        ("revert: pivot last", Revert("pivot last", MovePivotBackIntoTheMiddle)),
        ("revert: neutral compensations", Revert("neutral compensations", UnguardCompensations)),
        ("revert: idempotency keys", Revert("idempotency keys", MakeForwardsAdditive)),
        ("revert: declared fallback", Revert("declared fallback", DropFallback)),
        ("revert: idempotent reservation", Revert("idempotent reservation", MakeReserveAdditive)),
        ("revert: queryable pivot", Revert("queryable pivot", MakePivotOpaque)),
        ("lie: reservation declared idempotent but is not",
            Revert("a truthful idempotence declaration", BreakIdempotenceButKeepClaiming)),
        ("overreach: a compensation that undoes its neighbour too",
            Revert("a correctly scoped compensation", OverreachingCompensation))
    ];

    private static Saga Revert(string what, Func<Step, Step> mutate)
    {
        var source = Catalogue.V7_QueryablePivot();
        var rebuilt = new Saga($"v7 with '{what}' reverted", source.Schema, source.Initial, source.Steps.Select(mutate));
        foreach (var inv in source.Invariants)
        {
            rebuilt.Invariants.Add(inv);
        }

        foreach (var r in source.AcceptableResidue)
        {
            rebuilt.AcceptableResidue.Add(r);
        }

        return rebuilt;
    }

    private static Step MovePivotBackIntoTheMiddle(Step s) =>
        s.Name switch
        {
            // Reverting the reorder means the notification becomes the pivot again
            // and the capture goes back to being an ordinary retriable step. The
            // step list order is fixed by the catalogue, so the revert is expressed
            // as a change of kind rather than a change of position; the reachable
            // shape violation is the same either way.
            "SendConfirmation" => s with { Kind = StepKind.Pivot, Queryable = false },
            "CapturePayment" => s with { Kind = StepKind.Retriable, Queryable = false, CanBeRejected = false },
            _ => s
        };

    private static Step UnguardCompensations(Step s) =>
        s.Name switch
        {
            "ReserveStock" => s with { Compensate = st => st.Add("stock", 1).Add("reserved", -1) },
            "AuthorisePayment" => s with { Compensate = st => st.Add("authHold", -Catalogue.Amount) },
            _ => s
        };

    private static Step MakeForwardsAdditive(Step s) =>
        s.Name switch
        {
            "AuthorisePayment" => s with { Forward = st => st.Add("authHold", Catalogue.Amount), Idempotent = false },
            "SendConfirmation" => s with { Forward = st => st.Add("emails", 1), Idempotent = false },
            _ => s
        };

    private static Step DropFallback(Step s) =>
        s.Name == "AuthorisePayment" ? s with { CompensationFallback = null } : s;

    private static Step MakeReserveAdditive(Step s) =>
        s.Name == "ReserveStock"
            ? s with { Forward = st => st.Add("stock", -1).Add("reserved", 1), Idempotent = false }
            : s;

    private static Step MakePivotOpaque(Step s) => s.Kind == StepKind.Pivot ? s with { Queryable = false } : s;

    /// <summary>
    /// The only mutation here that is not a revert of a real fix.
    ///
    /// Every other property in this checker assumes the declarations are true:
    /// if a step says it is idempotent, the model believes it. That assumption is
    /// the largest single weakness of the whole approach, so it is worth knowing
    /// how much of it the checker can defend. This mutation removes the guard
    /// that makes the reservation idempotent while leaving the claim in place --
    /// the exact shape of a comment that says "safe to retry" above code that is
    /// not. The checker can catch this particular class of lie because the effect
    /// is a pure function it can apply twice and compare; it cannot catch the lie
    /// when the effect lives behind an HTTP call, which is the case that matters
    /// in production and is stated plainly in the limitations.
    /// </summary>
    private static Step BreakIdempotenceButKeepClaiming(Step s) =>
        s.Name == "ReserveStock"
            ? s with { Forward = st => st.Add("stock", -1).Add("reserved", 1) }
            : s;

    /// <summary>
    /// A compensation that cleans up more than it caused.
    ///
    /// `AllocateSlot` reads `reserved` and writes `slot`, so its author, undoing
    /// it, releases the slot -- and also releases the reservation, because from
    /// where they are sitting the reservation looks like part of the same
    /// booking. It is not: `ReserveStock` owns that variable and will release it
    /// itself when the rollback reaches it. The result is a double release.
    ///
    /// This is the bug the algebraic reverse-order check exists for. Each
    /// compensation is individually neutral, each is individually idempotent, and
    /// every property that looks at one step at a time is satisfied. Only the
    /// composition is wrong, and only running the whole prefix backwards shows
    /// it.
    /// </summary>
    private static Step OverreachingCompensation(Step s) =>
        s.Name == "AllocateSlot"
            ? s with { Compensate = st => st.With("slot", 0).Add("reserved", -1) }
            : s;
}

/// <summary>
/// Synthetic chains used only to measure how the state space grows. Every step is
/// well behaved: idempotent, neutrally compensated, never rejected. If the space
/// blows up here it blows up everywhere, because this is the cheapest saga of a
/// given length that the model can express.
/// </summary>
public static class Synthetic
{
    public static Saga Chain(int steps)
    {
        var names = Enumerable.Range(0, steps).Select(i => $"v{i}").ToArray();
        var schema = new Schema(names);
        var list = new List<Step>();
        for (var i = 0; i < steps; i++)
        {
            var v = names[i];
            list.Add(new Step
            {
                Name = $"s{i}",
                Service = $"svc{i % 3}",
                Kind = i == steps - 1 ? StepKind.Pivot : StepKind.Compensatable,
                Forward = st => st.With(v, 1),
                Compensate = i == steps - 1 ? null : st => st.With(v, 0),
                Idempotent = true,
                Queryable = i == steps - 1
            });
        }

        return new Saga($"synthetic chain of {steps}", schema, schema.State(), list);
    }
}
