namespace Sagas.Core;

/// <summary>How an operation's effect can be withdrawn, if at all.</summary>
public enum Reversibility
{
    /// <summary>Writes only to data the system owns. A compensation can be written.</summary>
    Internal,

    /// <summary>
    /// Leaves the system: an email, an SMS, a webhook, a file on a partner's SFTP,
    /// a regulatory filing. There is no inverse, only an apology.
    /// </summary>
    ExternallyVisible,

    /// <summary>
    /// Has no inverse but is guaranteed to succeed given enough retries -- a write
    /// to a store the system controls, with no business rule that can refuse it.
    /// </summary>
    GuaranteedToSucceed
}

/// <summary>One statement from the body of the original transaction.</summary>
public sealed record Operation(
    string Name,
    string Service,
    Reversibility Reversibility,
    IReadOnlyList<string> Reads,
    IReadOnlyList<string> Writes)
{
    public static Operation Of(
        string name,
        string service,
        Reversibility reversibility,
        string[]? reads = null,
        string[]? writes = null) =>
        new(name, service, reversibility, reads ?? [], writes ?? []);
}

public sealed record ExtractionPlan(
    IReadOnlyList<Operation> Order,
    IReadOnlyList<string> Rationale,
    int PivotIndex,
    int CrossServiceHandoffs)
{
    public string Describe() =>
        string.Join(
            Environment.NewLine,
            Order.Select((o, i) =>
                $"  {i + 1}. {o.Name,-20} {o.Service,-14} {Classify(o, i, PivotIndex)}"));

    /// <summary>
    /// The saga kind an operation would get, derived from what it actually is
    /// rather than from where it landed. A position-only rule reads well and
    /// lies: it labels an irreversible operation "compensatable" purely because
    /// the reorder could not move it, which is the one case where the label
    /// matters most.
    /// </summary>
    private static string Classify(Operation o, int i, int pivot)
    {
        if (i == pivot)
        {
            return "PIVOT";
        }

        return o.Reversibility switch
        {
            Reversibility.Internal when i < pivot => "compensatable",
            Reversibility.Internal => "compensatable, but pinned after the pivot -- its compensation is dead code",
            Reversibility.GuaranteedToSucceed when i > pivot => "retriable",
            Reversibility.GuaranteedToSucceed => "retriable, but pinned BEFORE the pivot -- cannot be rolled back",
            _ => "externally visible and not the pivot -- a second point of no return"
        };
    }
}

/// <summary>
/// Turns a monolithic transaction body into an ordered saga.
///
/// The interesting part is not the partitioning -- ops go to the service that
/// owns the data they write, which is nearly mechanical. It is the <b>ordering</b>.
/// A saga is only well formed if every irreversible operation happens after every
/// operation that might still cause an abort, and the original source order almost
/// never has that property, because inside a transaction it did not need to.
/// </summary>
public static class Extractor
{
    /// <summary>
    /// The extraction that respects the source and nothing else. Included so the
    /// report can check it and show what it costs, not as a recommendation.
    /// </summary>
    public static ExtractionPlan Naive(IReadOnlyList<Operation> ops)
    {
        var pivot = LastIndexOf(ops, Reversibility.ExternallyVisible);
        return new ExtractionPlan(
            ops,
            ["source order preserved; no reordering attempted"],
            pivot,
            CountHandoffs(ops));
    }

    /// <summary>
    /// Reorder so that all reversible work happens first, then the single point of
    /// no return, then work that cannot fail.
    ///
    /// This is a topological sort with a comparator, not a free permutation: real
    /// data dependencies (a write feeding a later read) are hard constraints and
    /// are never reordered across. Where the dependency graph leaves a choice, the
    /// tie-break is reversibility -- compensatable first, then guaranteed, then
    /// externally visible. That single tie-break is what turns source order into a
    /// well-formed saga.
    ///
    /// When a data dependency genuinely forces an irreversible operation to run
    /// before a fallible one, no reordering can fix it and the returned rationale
    /// says so. That case needs a design change -- usually splitting the
    /// irreversible operation into a reversible "prepare" and an irreversible
    /// "commit" -- and it is better to report it than to emit a plausible plan
    /// that is wrong.
    /// </summary>
    public static ExtractionPlan Reorder(IReadOnlyList<Operation> ops)
    {
        var n = ops.Count;
        var edges = new List<(int From, int To, string Why)>();

        // Hard constraints: true data dependencies in the original order.
        for (var a = 0; a < n; a++)
        {
            for (var b = a + 1; b < n; b++)
            {
                var flow = ops[a].Writes.Intersect(ops[b].Reads, StringComparer.Ordinal).ToList();
                var order = ops[a].Writes.Intersect(ops[b].Writes, StringComparer.Ordinal).ToList();
                var anti = ops[a].Reads.Intersect(ops[b].Writes, StringComparer.Ordinal).ToList();

                var reasons = new List<string>();
                if (flow.Count > 0)
                {
                    reasons.Add($"{ops[b].Name} reads {string.Join("/", flow)} written by {ops[a].Name}");
                }

                if (order.Count > 0)
                {
                    reasons.Add($"both write {string.Join("/", order)}; original order preserved");
                }

                if (anti.Count > 0)
                {
                    reasons.Add($"{ops[a].Name} reads {string.Join("/", anti)} before {ops[b].Name} overwrites it");
                }

                if (reasons.Count > 0)
                {
                    edges.Add((a, b, string.Join("; ", reasons)));
                }
            }
        }

        var indegree = new int[n];
        foreach (var (_, to, _) in edges)
        {
            indegree[to]++;
        }

        var rationale = new List<string>();
        var order2 = new List<int>();
        var remaining = Enumerable.Range(0, n).ToHashSet();

        while (remaining.Count > 0)
        {
            var ready = remaining.Where(i => indegree[i] == 0).ToList();
            if (ready.Count == 0)
            {
                // Cannot happen for edges derived from a total source order, but
                // an assertion is cheaper than a wrong plan.
                throw new InvalidOperationException("dependency cycle in a linear transaction body");
            }

            // The tie-break that does the work.
            var pick = ready
                .OrderBy(i => Rank(ops[i].Reversibility))
                .ThenBy(i => i)
                .First();

            if (ready.Count > 1)
            {
                var passedOver = ready.Where(i => i != pick && Rank(ops[i].Reversibility) > Rank(ops[pick].Reversibility))
                    .Select(i => ops[i].Name)
                    .ToList();
                if (passedOver.Count > 0)
                {
                    rationale.Add(
                        $"scheduled {ops[pick].Name} ({ops[pick].Reversibility}) ahead of " +
                        $"{string.Join(", ", passedOver)}: reversible work must finish before anything irreversible starts");
                }
            }

            order2.Add(pick);
            remaining.Remove(pick);
            foreach (var (from, to, _) in edges.Where(e => e.From == pick))
            {
                indegree[to]--;
            }
        }

        var result = order2.Select(i => ops[i]).ToList();
        var pivot = LastIndexOf(result, Reversibility.ExternallyVisible);

        for (var i = 0; i < result.Count; i++)
        {
            if (i < pivot && result[i].Reversibility != Reversibility.Internal)
            {
                rationale.Add(
                    $"UNFIXABLE BY REORDERING: {result[i].Name} is {result[i].Reversibility} but a data dependency " +
                    $"pins it before the pivot {result[pivot].Name}. Split it into a reversible prepare and an " +
                    "irreversible commit, or accept that an abort after this point leaves residue.");
            }

            if (i > pivot && result[i].Reversibility == Reversibility.Internal)
            {
                rationale.Add(
                    $"{result[i].Name} is reversible but is pinned after the pivot by a data dependency; " +
                    "its compensation is dead code and should be deleted rather than maintained");
            }
        }

        var handoffs = CountHandoffs(result);
        rationale.Add(
            $"service handoffs: {CountHandoffs(ops)} in source order, {handoffs} after reordering " +
            "(each handoff is a network boundary that can fail independently)");

        return new ExtractionPlan(result, rationale, pivot, handoffs);
    }

    /// <summary>
    /// Compensatable work first, then the point of no return, then work that
    /// cannot fail.
    ///
    /// The ordering that matters is Internal &lt; ExternallyVisible &lt;
    /// GuaranteedToSucceed, and the middle term is the one that catches people
    /// out. An operation that cannot be undone but always succeeds sounds safer
    /// than one that is externally visible, so the instinct is to schedule it
    /// earlier. It is not safer: it is equally impossible to undo, so putting it
    /// before the pivot means a later abort cannot roll back past it. Guaranteed
    /// success buys the right to sit *after* the pivot, not the right to sit
    /// early.
    /// </summary>
    private static int Rank(Reversibility r) => r switch
    {
        Reversibility.Internal => 0,
        Reversibility.ExternallyVisible => 1,
        _ => 2
    };

    private static int LastIndexOf(IReadOnlyList<Operation> ops, Reversibility r)
    {
        for (var i = ops.Count - 1; i >= 0; i--)
        {
            if (ops[i].Reversibility == r)
            {
                return i;
            }
        }

        return -1;
    }

    private static int CountHandoffs(IReadOnlyList<Operation> ops)
    {
        var handoffs = 0;
        for (var i = 1; i < ops.Count; i++)
        {
            if (!string.Equals(ops[i].Service, ops[i - 1].Service, StringComparison.Ordinal))
            {
                handoffs++;
            }
        }

        return handoffs;
    }

    /// <summary>The transaction body from <see cref="Catalogue"/>, as the extractor sees it.</summary>
    public static IReadOnlyList<Operation> OrderTransaction =>
    [
        Operation.Of("ReserveStock", "inventory", Reversibility.Internal,
            reads: ["stock"], writes: ["stock", "reserved"]),
        Operation.Of("AuthorisePayment", "payments", Reversibility.Internal,
            writes: ["authHold"]),
        Operation.Of("SendConfirmation", "notifications", Reversibility.ExternallyVisible,
            reads: ["reserved"], writes: ["emails"]),
        Operation.Of("AllocateSlot", "warehouse", Reversibility.Internal,
            reads: ["reserved"], writes: ["slot"]),
        Operation.Of("CapturePayment", "payments", Reversibility.GuaranteedToSucceed,
            reads: ["authHold"], writes: ["authHold", "captured"]),
        Operation.Of("Dispatch", "logistics", Reversibility.GuaranteedToSucceed,
            reads: ["slot", "captured"], writes: ["dispatched"])
    ];

    /// <summary>
    /// The same six operations, with one property changed: the notification
    /// service accepts an idempotency key, so a confirmation can be re-sent
    /// safely and the send is guaranteed to succeed rather than externally
    /// visible.
    ///
    /// That single boolean moves the point of no return from step 4 to step 5 and
    /// produces a different saga. It is a fact about someone else's HTTP API, it
    /// does not appear anywhere in the original transaction, and it is the most
    /// consequential input to the whole extraction.
    /// </summary>
    public static IReadOnlyList<Operation> OrderTransactionWithDedupedEmail =>
        OrderTransaction
            .Select(o => o.Name switch
            {
                "SendConfirmation" => o with { Reversibility = Reversibility.GuaranteedToSucceed },
                "CapturePayment" => o with { Reversibility = Reversibility.ExternallyVisible },
                _ => o
            })
            .ToList();
}
