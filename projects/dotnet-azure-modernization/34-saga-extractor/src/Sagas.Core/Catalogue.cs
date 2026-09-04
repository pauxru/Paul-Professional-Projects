namespace Sagas.Core;

/// <summary>
/// One business transaction, modelled five times.
///
/// Version 1 is what falls out of the monolith if you cut it where the method
/// boundaries already are. Each later version fixes exactly what
/// <see cref="Checker"/> found in the one before, and each fix exposes the next
/// problem. Nothing here was designed by looking ahead; the sequence is the
/// order the counterexamples arrived in, which is why version 4 fixes something
/// that looks obvious in hindsight and was not obvious at version 1.
///
/// The monolithic original, for reference:
/// <code>
/// using (var tx = new TransactionScope())
/// {
///     _inventory.Reserve(orderId, sku, 1);
///     _payments.Authorize(orderId, amount);
///     _notifications.SendConfirmation(orderId);
///     _warehouse.AllocateSlot(orderId);
///     _payments.Capture(orderId);
///     _logistics.Dispatch(orderId);
///     tx.Complete();
/// }
/// </code>
/// Six operations, four services, one ACID boundary that stops existing the
/// moment those four services stop sharing a database.
/// </summary>
public static class Catalogue
{
    public const int InitialStock = 5;
    public const int Amount = 100;

    public static Schema OrderSchema { get; } = new(
        "stock", "reserved", "authHold", "captured", "slot", "emails", "dispatched");

    public static WorldState OrderStart => OrderSchema.State(("stock", InitialStock));

    // ---------------------------------------------------------------- effects

    private static WorldState ReserveNaive(WorldState s) => s.Add("stock", -1).Add("reserved", 1);

    private static WorldState ReleaseNaive(WorldState s) => s.Add("stock", 1).Add("reserved", -1);

    /// <summary>
    /// Release only if a reservation is actually held. The guard is the whole
    /// difference between version 2 and version 3, and it is four characters of
    /// C# that took an exhaustive search to justify.
    /// </summary>
    private static WorldState ReleaseGuarded(WorldState s) =>
        s["reserved"] > 0 ? s.Add("stock", 1).Add("reserved", -1) : s;

    private static WorldState AuthoriseAdditive(WorldState s) => s.Add("authHold", Amount);

    /// <summary>
    /// The same operation carrying an idempotency key: a second request with the
    /// same key returns the first hold rather than placing another.
    /// </summary>
    private static WorldState AuthoriseKeyed(WorldState s) => s.With("authHold", Amount);

    private static WorldState VoidAdditive(WorldState s) => s.Add("authHold", -Amount);

    private static WorldState VoidGuarded(WorldState s) => s["authHold"] > 0 ? s.With("authHold", 0) : s;

    private static WorldState SendEmail(WorldState s) => s.Add("emails", 1);

    private static WorldState SendEmailDeduplicated(WorldState s) => s.With("emails", 1);

    private static WorldState AllocateSlot(WorldState s) => s.With("slot", 1);

    private static WorldState ReleaseSlot(WorldState s) => s["slot"] > 0 ? s.With("slot", 0) : s;

    private static WorldState Capture(WorldState s) =>
        s["authHold"] > 0 ? s.With("authHold", 0).With("captured", Amount) : s;

    private static WorldState Dispatch(WorldState s) => s.With("dispatched", 1);

    // ------------------------------------------------------------ invariants

    private static Saga WithBusinessRules(this Saga saga) => saga
        .Invariant("stock is never negative", s => s["stock"] >= 0)
        .Invariant("a reservation count is never negative", s => s["reserved"] >= 0)
        .Invariant("at most one unit is reserved for this order", s => s["reserved"] <= 1)
        .Invariant(
            $"units are conserved: stock + reserved == {InitialStock}",
            s => s["stock"] + s["reserved"] == InitialStock)
        .Invariant("the customer is never charged twice", s => s["captured"] <= Amount)
        .Invariant("no more than one authorisation hold exists", s => s["authHold"] <= Amount)
        .Invariant("at most one confirmation email is sent", s => s["emails"] <= 1)
        .Invariant("nothing is dispatched without payment captured",
            s => s["dispatched"] == 0 || s["captured"] == Amount);

    // -------------------------------------------------------------- versions

    /// <summary>
    /// Cut where the monolith's method calls already were, keeping the original
    /// order. This is what an automated extraction gives you if it respects the
    /// source and nothing else -- see <see cref="Extractor.Naive"/>.
    /// </summary>
    public static Saga V1_AsWritten() => new Saga(
        "v1 -- extracted in source order",
        OrderSchema,
        OrderStart,
        [
            new Step
            {
                Name = "ReserveStock", Service = "inventory", Kind = StepKind.Compensatable,
                Forward = ReserveNaive, Compensate = ReleaseNaive
            },
            new Step
            {
                Name = "AuthorisePayment", Service = "payments", Kind = StepKind.Compensatable,
                Forward = AuthoriseAdditive, Compensate = VoidAdditive
            },
            new Step
            {
                // You cannot un-send an email. That makes this the pivot wherever
                // it sits, and in source order it sits in the middle.
                Name = "SendConfirmation", Service = "notifications", Kind = StepKind.Pivot,
                Forward = SendEmail, CanBeRejected = true
            },
            new Step
            {
                Name = "AllocateSlot", Service = "warehouse", Kind = StepKind.Compensatable,
                Forward = AllocateSlot, Compensate = ReleaseSlot
            },
            new Step
            {
                Name = "CapturePayment", Service = "payments", Kind = StepKind.Retriable,
                Forward = Capture, CanBeRejected = false
            },
            new Step
            {
                Name = "Dispatch", Service = "logistics", Kind = StepKind.Retriable,
                Forward = Dispatch, CanBeRejected = false
            }
        ]).WithBusinessRules();

    /// <summary>Same operations, reordered so the pivot is last. Nothing else changed.</summary>
    public static Saga V2_PivotLast() => new Saga(
        "v2 -- pivot moved to the end",
        OrderSchema,
        OrderStart,
        [
            new Step
            {
                Name = "ReserveStock", Service = "inventory", Kind = StepKind.Compensatable,
                Forward = ReserveNaive, Compensate = ReleaseNaive
            },
            new Step
            {
                Name = "AuthorisePayment", Service = "payments", Kind = StepKind.Compensatable,
                Forward = AuthoriseAdditive, Compensate = VoidAdditive
            },
            new Step
            {
                Name = "AllocateSlot", Service = "warehouse", Kind = StepKind.Compensatable,
                Forward = AllocateSlot, Compensate = ReleaseSlot
            },
            new Step
            {
                Name = "CapturePayment", Service = "payments", Kind = StepKind.Pivot,
                Forward = Capture, CanBeRejected = true
            },
            new Step
            {
                Name = "Dispatch", Service = "logistics", Kind = StepKind.Retriable,
                Forward = Dispatch, CanBeRejected = false
            },
            new Step
            {
                Name = "SendConfirmation", Service = "notifications", Kind = StepKind.Retriable,
                Forward = SendEmail, CanBeRejected = false
            }
        ]).WithBusinessRules();

    /// <summary>Version 2 with compensations that are neutral when the step did not run.</summary>
    public static Saga V3_NeutralCompensations() => Rebuild(
        "v3 -- compensations neutral on an unrun step",
        V2_PivotLast(),
        s => s.Name switch
        {
            "ReserveStock" => s with { Compensate = ReleaseGuarded },
            "AuthorisePayment" => s with { Compensate = VoidGuarded },
            _ => s
        });

    /// <summary>Version 3 with idempotency keys on the forward operations.</summary>
    public static Saga V4_IdempotentForwards() => Rebuild(
        "v4 -- idempotency keys on forward operations",
        V3_NeutralCompensations(),
        s => s.Name switch
        {
            "AuthorisePayment" => s with { Forward = AuthoriseKeyed, Idempotent = true },
            "SendConfirmation" => s with { Forward = SendEmailDeduplicated, Idempotent = true },
            "AllocateSlot" => s with { Idempotent = true },
            "ReserveStock" => s with { MaxAttempts = 1 },
            _ => s
        });

    /// <summary>
    /// Version 4, but honest about the fact that voiding an authorisation can be
    /// definitively refused -- the hold expired, the issuer declined the reversal.
    /// Included to be checked and to fail. It is version 4 with one flag changed.
    /// </summary>
    public static Saga V4b_VoidCanBeRefused() => Rebuild(
        "v4b -- voiding an authorisation can be refused",
        V4_IdempotentForwards(),
        s => s.Name == "AuthorisePayment" ? s with { CompensationCanBeRejected = true } : s);

    /// <summary>
    /// The version that survives. A refused void is absorbed by making the hold's
    /// own expiry the compensation of last resort: the money is never captured,
    /// the hold lapses, and the residue is declared rather than pretended away.
    /// </summary>
    public static Saga V5_DeclaredResidue()
    {
        var saga = Rebuild(
            "v5 -- refused voids fall back to hold expiry, residue declared",
            V4b_VoidCanBeRefused(),
            s => s.Name == "AuthorisePayment"
                ? s with { CompensationFallback = HoldLapses, CompensationIdempotent = true }
                : s);
        saga.AllowResidue("authHold");
        return saga;
    }

    /// <summary>
    /// The fallback is the identity function, and that is the point: nothing more
    /// is done, the hold simply remains until the issuer drops it. Writing it as
    /// an effect rather than as a comment is what forces the residue to be
    /// declared and therefore reviewed.
    /// </summary>
    private static WorldState HoldLapses(WorldState s) => s;

    /// <summary>
    /// Version 5 with a reservation that is safe to apply twice. The crash-after-
    /// effect path was landing it a second time and quietly reserving two units
    /// against a one-unit order; the guard makes the second application a no-op.
    /// </summary>
    public static Saga V6_IdempotentReservation() => Rebuild(
        "v6 -- reservation is idempotent",
        V5_DeclaredResidue(),
        s => s.Name == "ReserveStock"
            ? s with { Idempotent = true, Forward = ReserveIdempotent, Compensate = ReleaseGuarded, MaxAttempts = 2 }
            : s);

    /// <summary>
    /// Version 6 with a pivot that can be asked what happened.
    ///
    /// This is the last fix and the one that was least obvious from the design
    /// document, because it is not a property of the saga at all -- it is a
    /// requirement on the payment provider's API. An ambiguous timeout on the
    /// pivot cannot be resolved by anything the orchestrator does; the
    /// counterexample in section 5 is four transitions long and has no fix inside
    /// the saga.
    /// </summary>
    public static Saga V7_QueryablePivot() => Rebuild(
        "v7 -- the pivot can be asked what happened",
        V6_IdempotentReservation(),
        s => s.Kind == StepKind.Pivot ? s with { Queryable = true } : s);

    private static WorldState ReserveIdempotent(WorldState s) =>
        s["reserved"] > 0 ? s : s.Add("stock", -1).Add("reserved", 1);

    private static Saga Rebuild(string name, Saga source, Func<Step, Step> transform)
    {
        var rebuilt = new Saga(name, source.Schema, source.Initial, source.Steps.Select(transform));
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

    /// <summary>
    /// The versions in the order the checker rejected them. The note is the
    /// single change from the previous version -- one fix per version, so that
    /// every row of the results table attributes its delta to exactly one
    /// decision.
    /// </summary>
    public static IReadOnlyList<(string Label, string Note, Func<Saga> Build)> Progression =>
    [
        ("v1", "as extracted, compensations written by hand", V1_AsWritten),
        ("v2", "pivot moved to its correct position", V2_PivotLast),
        ("v3", "compensations made neutral when the step did not land", V3_NeutralCompensations),
        ("v4", "idempotency keys on forward steps", V4_IdempotentForwards),
        ("v4b", "(branch) the void call can itself be refused", V4b_VoidCanBeRefused),
        ("v5", "declared fallback and signed-off residue", V5_DeclaredResidue),
        ("v6", "reservation made idempotent, restoring the retry", V6_IdempotentReservation),
        ("v7", "pivot made queryable by the provider", V7_QueryablePivot)
    ];
}
