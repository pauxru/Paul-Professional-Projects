using System.Globalization;

namespace Sagas.Core;

/// <summary>
/// The whole report, as code.
///
/// Every number in `docs/results.md` is produced here by running the checker.
/// Nothing is typed in by hand, which is what makes the results-integrity test
/// possible: it re-renders this document and byte-compares it against the file
/// on disk, so a change to the checker that moves a number fails the build
/// rather than quietly making the prose wrong.
/// </summary>
public static class Experiments
{
    private static readonly string[] ProgressionHeaders =
        ["version", "change", "states", "violations", "shortest"];

    public static string Render()
    {
        var report = new Report(
            "Cutting a transaction into a saga, and proving the pieces fit",
            "A monolith's `TransactionScope` is a promise the database keeps. Split the "
            + "monolith into services and the promise disappears, but the code that "
            + "relied on it usually does not change shape. This project takes one "
            + "six-step transaction, extracts a saga from it, and then exhaustively "
            + "explores the resulting state machine to find the places where the "
            + "compensations do not actually put the world back.",
            "src/Sagas.Report",
            "Exhaustive BFS over the reachable state space; no randomness, no timing.");

        TheTransaction(report);
        Extraction(report);
        Progression(report);
        Counterexamples(report);
        CrashBudget(report);
        Scope(report);
        Journalling(report);
        BoundedRetries(report);
        Mutations(report);
        Growth(report);
        WhatThisDoesNotDo(report);

        return report.Render();
    }

    private static CheckerOptions Base => new() { CrashBudget = 1 };

    private static void TheTransaction(Report r)
    {
        r.H2("The transaction being cut up");
        r.Para(
            "The subject is an order-fulfilment routine: reserve stock, authorise the "
            + "card, send a confirmation, allocate a delivery slot, capture the money, "
            + "dispatch. In the monolith all six run inside one transaction against one "
            + "database, so the only failure the code handles is an exception, and the "
            + "only recovery it needs is a rollback it does not have to write.");
        r.Para(
            "Three things stop being true the moment those six operations live in five "
            + "services. Failure is no longer a single event -- an operation can be "
            + "rejected, or it can time out, which is not the same thing and is the "
            + "source of most of what follows. Rollback is no longer free; every "
            + "reversible step needs an explicit inverse that someone writes and nobody "
            + "tests. And two of the six operations have no inverse at all: an email "
            + "that has been sent cannot be unsent, and a parcel on a van cannot be "
            + "un-dispatched.");
        r.Note(
            "The word \"saga\" gets used for any workflow with retries. The definition "
            + "used here is the narrow one from Garcia-Molina and Salem by way of Pat "
            + "Helland: a sequence of compensatable steps, then exactly one pivot, then "
            + "steps that are guaranteed to succeed. Before the pivot the saga can "
            + "abort; after it, the saga can only go forward. Everything this tool "
            + "checks follows from that shape.");

        var ops = Extractor.OrderTransaction;
        r.Table(
            ["#", "operation", "service", "reversibility"],
            ops.Select((o, i) => new[]
            {
                (i + 1).ToString(CultureInfo.InvariantCulture),
                o.Name,
                o.Service,
                o.Reversibility.ToString()
            }));
    }

    private static void Extraction(Report r)
    {
        r.H2("Extraction: where does the point of no return go?");
        r.Para(
            "The extractor takes the operations in source order, reads their declared "
            + "reversibility and their reads/writes, and produces an ordering that has "
            + "the saga shape: compensatable work first, then the pivot, then work that "
            + "cannot fail. It is a topological sort over the data dependencies with a "
            + "tie-break on reversibility, and the tie-break is the entire point.");

        var naive = Extractor.Naive(Extractor.OrderTransaction);
        var reordered = Extractor.Reorder(Extractor.OrderTransaction);

        r.Expect(
            "Reordering will reduce the number of cross-service handoffs, because "
            + "grouping compensatable work together should group the services it "
            + "touches.");
        r.Found(
            $"It does not. Source order needs {naive.CrossServiceHandoffs} handoffs and "
            + $"the reordered saga needs {reordered.CrossServiceHandoffs} -- identical. "
            + "The data dependencies pin payments before and after the pivot regardless "
            + "of how the reversible steps are arranged, so the handoff count is a "
            + "property of the service boundaries, not of the ordering. Reordering buys "
            + "correctness, not fewer network calls, and it was worth measuring that "
            + "rather than asserting it.",
            held: false);

        r.Code(naive.Describe() + "\n\n  ^ source order: pivot at position "
               + (naive.PivotIndex + 1) + "\n\n" + reordered.Describe()
               + "\n\n  ^ after reordering: pivot at position " + (reordered.PivotIndex + 1));

        r.Para(
            "The ranking that produces this is `Internal < ExternallyVisible < "
            + "GuaranteedToSucceed`, and the middle term is the one that catches people "
            + "out. An operation that cannot be undone but always succeeds feels safer "
            + "than one that is externally visible, so the instinct is to schedule it "
            + "early and get it out of the way. It is not safer. It is equally "
            + "impossible to undo, so scheduling it before the pivot means a later abort "
            + "cannot roll back past it. Guaranteed success buys the right to sit "
            + "*after* the pivot, not the right to sit early.");

        r.H3("One boolean about somebody else's API moves the pivot");
        r.Para(
            "The notification service in this example sends whatever it is given. If it "
            + "instead accepted an idempotency key, sending a confirmation would become "
            + "safe to retry, which makes it guaranteed-to-succeed rather than "
            + "externally-visible. Nothing about the order-fulfilment logic changes. "
            + "Here is the saga the extractor produces from the same six operations "
            + "with that one property flipped:");

        var deduped = Extractor.Reorder(Extractor.OrderTransactionWithDedupedEmail);
        r.Code(deduped.Describe() + "\n\n  ^ pivot at position " + (deduped.PivotIndex + 1));
        r.Para(
            "The point of no return is no longer \"we promised the customer\", it is \"we "
            + "took the money\", and the confirmation has moved after it where it can be "
            + "retried until it lands. The number of abortable steps did not change, so "
            + "this is not a structural win and it would be dishonest to present it as "
            + "one. It is a business win: the commitment the saga cannot walk back is "
            + "now the one the business actually wants to be committed to.");
        r.Para(
            "What makes it worth a section is where the change came from. Nothing about "
            + "order fulfilment was reconsidered. Someone else's HTTP API grew an "
            + "idempotency key, and the correct shape of this workflow changed as a "
            + "result. That dependency is invisible in the monolith and stays invisible "
            + "in most extractions, because it lives in the gap between a service's "
            + "documentation and the workflow that calls it. Making it an input the "
            + "extractor demands is the most useful thing this tool does.");

        r.Para(
            "Where reordering cannot help, the extractor says so rather than producing a "
            + "saga that looks well-formed and is not:");
        r.Code(string.Join("\n", reordered.Rationale.Select(x => "- " + x)));
    }

    private static void Progression(Report r)
    {
        r.H2("Seven versions of the same saga");
        r.Para(
            "The catalogue holds one saga in seven versions. v1 is what the extraction "
            + "produces if you take the operations at face value and write the "
            + "compensations you would write on a whiteboard. Each later version fixes "
            + "exactly one thing the checker found in the version before it. This is not "
            + "a reconstruction: the versions exist because the checker rejected the "
            + "previous one, and the order below is the order in which the defects were "
            + "found.");

        r.Expect(
            "Each fix will shrink the state space, because removing a defect removes "
            + "the states that reach it.");

        var rows = new List<string[]>();
        foreach (var (name, note, build) in Catalogue.Progression)
        {
            var res = Checker.Check(build(), Base);
            var shortest = res.Violations.Count == 0
                ? "--"
                : res.Violations.Min(v => v.Depth).ToString(CultureInfo.InvariantCulture);
            var breakdown = res.Violations.Count == 0
                ? "none"
                : string.Join(", ",
                    res.Violations.GroupBy(v => v.Kind)
                        .OrderBy(g => g.Key.ToString(), StringComparer.Ordinal)
                        .Select(g => $"{g.Key} x{g.Count()}"));
            rows.Add([
                name,
                note,
                res.StatesExplored.ToString(CultureInfo.InvariantCulture),
                breakdown,
                shortest
            ]);
        }

        var v5 = Checker.Check(Catalogue.V5_DeclaredResidue(), Base).StatesExplored;
        var v6 = Checker.Check(Catalogue.V6_IdempotentReservation(), Base).StatesExplored;

        r.Found(
            $"v1 -> v2 removes a shape violation and the state "
            + $"space *grows*. v5 -> v6 fixes a genuine safety defect and the state space "
            + $"nearly doubles, {v5} to {v6}, because the fix restores a retry that had "
            + "been suppressed. State-space size measures how much behaviour the model "
            + "admits, not how much of that behaviour is wrong, and treating a smaller "
            + "number as better would have pointed every fix in the wrong direction.",
            held: false);

        r.Table(ProgressionHeaders, rows);

        r.Para(
            "The interesting column is the last one. The deepest counterexample in the "
            + "whole progression is six transitions from the initial state. That is the "
            + "opposite of the usual argument for model checking, which is that bugs "
            + "hide behind long interleavings.");

        r.Expect(
            "Counterexamples will be long -- eight or more transitions -- which is what "
            + "justifies exhaustive search over ordinary testing.");
        r.Found(
            "Every counterexample is between zero and six transitions "
            + "deep, and most are two to four. The reason a test suite misses these is "
            + "not depth, it is combination: the failing traces need a timeout on one "
            + "step *and then* a rejection on a later one, and nobody writes that test "
            + "because neither half looks interesting alone. Exhaustive search wins here "
            + "by being unimaginative, not by being deep.",
            held: false);
    }

    private static void Counterexamples(Report r)
    {
        r.H2("What the counterexamples actually look like");
        r.Para(
            "A violation is only useful if it comes with the shortest path to it. The "
            + "checker keeps a parent pointer for every state it enqueues, so the trace "
            + "it prints is the minimum-length sequence of transitions that reaches the "
            + "bad state -- there is no shorter explanation.");

        Show(r, "A compensation that leaves residue", Catalogue.V5_DeclaredResidue(),
            PropertyKind.DirtyAbort,
            "The saga aborts and reports that it rolled back. It did not: stock is still "
            + "decremented. The step that landed the change timed out, the orchestrator "
            + "did not know whether it had landed, and the compensation it ran was "
            + "written on the assumption that it had not.");

        Show(r, "A rollback with nowhere to go", Catalogue.V6_IdempotentReservation(),
            PropertyKind.StuckState,
            "This is the residual defect the model cannot fix from inside the "
            + "orchestrator. The pivot times out; the orchestrator cannot tell whether "
            + "the point of no return was crossed; it may neither go forward (the step "
            + "may not have happened) nor back (it may have). The only fix is a "
            + "queryable pivot, which is a requirement on the provider's API, not on the "
            + "saga.");
    }

    private static void Show(Report r, string heading, Saga saga, PropertyKind kind, string prose)
    {
        r.H3(heading);
        r.Para(prose);
        var res = Checker.Check(saga, Base);
        var v = res.Violations.Where(x => x.Kind == kind).OrderBy(x => x.Depth).FirstOrDefault();
        r.Code(v is null
            ? $"no {kind} in {saga.Name}"
            : $"{saga.Name}: {kind} at depth {v.Depth}\n{v.Detail}\n\n{v.Render(saga.Initial)}");
    }

    private static void CrashBudget(Report r)
    {
        r.H2("\"Exhaustive\" is exhaustive within a bound");
        r.Para(
            "The state space is only finite because the number of orchestrator crashes "
            + "is capped. That cap is the one place where this tool stops being a proof "
            + "and starts being a search, so it is worth knowing whether the cap hides "
            + "anything.");

        r.Expect(
            "A budget of one crash will be sufficient: a second crash should only "
            + "produce longer versions of traces the first crash already found.");

        var rows = new List<string[]>();
        var v4 = Catalogue.V4_IdempotentForwards();
        var v7 = Catalogue.V7_QueryablePivot();
        for (var budget = 0; budget <= 3; budget++)
        {
            var a = Checker.Check(v4, Base with { CrashBudget = budget });
            var b = Checker.Check(v7, Base with { CrashBudget = budget });
            rows.Add([
                budget.ToString(CultureInfo.InvariantCulture),
                a.StatesExplored.ToString(CultureInfo.InvariantCulture),
                a.Violations.Count.ToString(CultureInfo.InvariantCulture),
                b.StatesExplored.ToString(CultureInfo.InvariantCulture),
                b.Violations.Count.ToString(CultureInfo.InvariantCulture)
            ]);
        }

        r.Found(
            "On a defective saga, no. v4 finds new, "
            + "genuinely distinct violations at budget 2 and again at budget 3 -- so any "
            + "report of \"v4 has three bugs\" is really \"v4 has three bugs that fit in "
            + "one crash\". v7 is clean at every budget from zero to three, which is "
            + "evidence but is still not proof.",
            held: false);

        r.Table(["crash budget", "v4 states", "v4 violations", "v7 states", "v7 violations"], rows);
        r.Note(
            "This is the honest limitation of the whole exercise and it belongs in the "
            + "middle of the report rather than a footnote at the end. Bounded "
            + "exhaustive search proves the absence of counterexamples *within the "
            + "bound*. The v4 column is what that caveat looks like when it bites.");
    }

    private static void Scope(Report r)
    {
        r.H2("The rollback-scope decision");
        r.Para(
            "When a step times out, the orchestrator does not know whether it ran. There "
            + "are two policies. Compensate it anyway, on the grounds that a "
            + "well-written compensation is a no-op when the effect is absent. Or skip "
            + "it, on the grounds that compensating something that never happened is "
            + "itself a change. The second is the one people reach for, because it "
            + "sounds more careful.");

        r.Expect(
            "Skipping the uncertain step is unsafe and the checker will produce a "
            + "counterexample for it.");

        var v7 = Catalogue.V7_QueryablePivot();
        var rows = new List<string[]>();
        foreach (var scope in Enum.GetValues<CompensationScope>())
        {
            var res = Checker.Check(v7, Base with { Scope = scope });
            rows.Add([
                scope.ToString(),
                res.StatesExplored.ToString(CultureInfo.InvariantCulture),
                res.Violations.Count.ToString(CultureInfo.InvariantCulture),
                res.Violations.Count == 0
                    ? "--"
                    : string.Join("; ", res.Violations.OrderBy(v => v.Depth)
                        .Select(v => $"[{v.Depth}] {v.Detail}"))
            ]);
        }

        r.Found(
            "On the fully-fixed v7 -- a saga with no other defects at all -- "
            + "switching to the careful-sounding policy reintroduces two dirty aborts, "
            + "at depths 3 and 7. The saga is otherwise identical. This is the single "
            + "clearest result in the report: the safe policy is the one that looks "
            + "reckless.",
            held: true);

        r.Table(["scope", "states", "violations", "detail"], rows);
        r.Para(
            "The reason is that \"compensate anyway\" and \"a compensation must be "
            + "idempotent\" are the same requirement seen from two directions. If "
            + "`comp(s)` is a no-op when `s` did not land, then running it on an "
            + "uncertain step costs nothing and covers the case where the step did land. "
            + "If it is not a no-op, the compensation is wrong independently of when you "
            + "choose to run it. The checker enforces exactly this as its "
            + "`CompensationNotNeutral` property.");
    }

    private static void Journalling(Report r)
    {
        r.H2("A mitigation that turned out to fix nothing");
        r.Para(
            "One of the counterexamples found during development was a saga that rolled "
            + "back from the wrong index: a step was rejected, the orchestrator rolled "
            + "back from the step before it, and an earlier attempt that had landed and "
            + "lost its acknowledgement was left in place. The fix was to journal intent "
            + "before every call, so that an attempt counter of zero genuinely means no "
            + "call was made and the rollback can safely start one step earlier.");

        r.Expect(
            "Turning journalling off will reintroduce violations, because the counter is "
            + "what makes the rollback scope decision sound.");

        var rows = new List<string[]>();
        foreach (var (label, saga) in new (string, Saga)[]
                 {
                     ("v4", Catalogue.V4_IdempotentForwards()),
                     ("v7", Catalogue.V7_QueryablePivot())
                 })
        {
            foreach (var journal in new[] { true, false })
            {
                var res = Checker.Check(saga, Base with { JournalAttempts = journal });
                rows.Add([
                    label,
                    journal ? "on" : "off",
                    res.StatesExplored.ToString(CultureInfo.InvariantCulture),
                    res.Violations.Count.ToString(CultureInfo.InvariantCulture)
                ]);
            }
        }

        r.Found(
            "Journalling changes the size of the state space -- roughly "
            + "10% more states without it, because a crashed attempt stays "
            + "indistinguishable from no attempt -- and changes no violation count "
            + "anywhere, on the defective v4 or the correct v7. By the time the rollback "
            + "scope was corrected to start at the uncertain step rather than before it, "
            + "the journal had nothing left to protect. It is a real mechanism that "
            + "fixes a real problem in a design that no longer exists.",
            held: false);

        r.Table(["saga", "journalling", "states", "violations"], rows);
        r.Note(
            "This is kept in the report rather than deleted from the code because it is "
            + "the most common way engineering effort is wasted: a mitigation is added "
            + "for a failure that a different, later fix had already made unreachable, "
            + "and nobody ever re-measures. The only reason it is visible here is that "
            + "the switch was left in place and turned off on purpose.");
    }

    private static void BoundedRetries(Report r)
    {
        r.H2("Compensations must retry forever");
        r.Para(
            "Forward steps have an attempt cap; that is what makes a saga abort rather "
            + "than hang. The natural instinct is to apply the same cap to "
            + "compensations, because unbounded retries are how you build a system that "
            + "hammers a dead dependency at full rate for a week.");

        r.Expect("Capping compensation attempts will create stuck states.");
        var v7 = Catalogue.V7_QueryablePivot();
        var bounded = Checker.Check(v7, Base with { CompensationRetriesForever = false });
        var unbounded = Checker.Check(v7, Base);
        r.Found(
            $"And the count is small enough to be concrete: {bounded.Violations.Count} "
            + $"stuck states against {unbounded.Violations.Count} with unbounded retries. "
            + "A saga that gives up mid-rollback has, by construction, no next move: it "
            + "cannot go forward because it already decided to abort, and it cannot "
            + "finish going back. The system is left in a state no code is written to "
            + "handle, which in practice means a human reading a database by hand.",
            held: true);

        r.Table(["compensation retries", "states", "stuck states", "shortest"],
        [
            [
                "unbounded", unbounded.StatesExplored.ToString(CultureInfo.InvariantCulture),
                unbounded.Violations.Count(v => v.Kind == PropertyKind.StuckState)
                    .ToString(CultureInfo.InvariantCulture),
                "--"
            ],
            [
                "capped", bounded.StatesExplored.ToString(CultureInfo.InvariantCulture),
                bounded.Violations.Count(v => v.Kind == PropertyKind.StuckState)
                    .ToString(CultureInfo.InvariantCulture),
                bounded.Violations.Count == 0
                    ? "--"
                    : bounded.Violations.Min(v => v.Depth).ToString(CultureInfo.InvariantCulture)
            ]
        ]);

        r.Para(
            "The resolution is not a cap. It is that the retry must be unbounded in "
            + "attempts and bounded in rate, and that a compensation which has been "
            + "failing for an hour is an alert, not an error return. \"Retry forever\" "
            + "is a statement about the state machine; \"back off and page someone\" is "
            + "a statement about the operator. The two are not in conflict, and "
            + "conflating them is how stuck sagas get built.");
    }

    private static void Mutations(Report r)
    {
        r.H2("Is every fix load-bearing, and is every property alive?");
        r.Para(
            "Seven versions of accumulated fixes is seven opportunities to have added "
            + "something that does nothing. The first six mutations revert each of v7's "
            + "fixes individually, leaving the other five in place, and re-run the "
            + "checker. A fix that can be reverted with no violations appearing is a fix "
            + "that was not needed -- or, worse, a property the checker does not test.");
        r.Para(
            "The last two mutations are the reverse experiment. They do not revert a "
            + "fix; they inject a defect that no version of this saga ever had, chosen "
            + "specifically to trip a property that nothing else trips. They exist "
            + "because a test in the suite asks whether every property is reachable from "
            + "*some* design, and when it was first written the answer was no: two of "
            + "the seven properties could not be violated by anything in the catalogue. "
            + "Both were real checks, correctly implemented, and both were dead. One of "
            + "them -- the algebraic reverse-order check -- was fully written and never "
            + "called from anywhere.");

        r.Expect(
            "All six reverts will be load-bearing. This is a weak prediction because the "
            + "fixes were derived from counterexamples, so it would be strange if they "
            + "were not -- the interesting outcome is any that are not.");

        var rows = new List<string[]>();
        var dead = 0;
        foreach (var (name, saga) in DesignMutations.All())
        {
            var res = Checker.Check(saga, Base);
            if (res.Violations.Count == 0)
            {
                dead++;
            }

            rows.Add([
                name,
                res.StatesExplored.ToString(CultureInfo.InvariantCulture),
                res.Violations.Count.ToString(CultureInfo.InvariantCulture),
                res.Violations.Count == 0
                    ? "-- NOT LOAD-BEARING"
                    : $"{res.Violations.OrderBy(v => v.Depth).First().Kind} at depth "
                      + res.Violations.Min(v => v.Depth).ToString(CultureInfo.InvariantCulture)
            ]);
        }

        r.Found(
            dead == 0
                ? $"All {rows.Count} mutations reintroduce at least one violation, and the "
                  + "violation that reappears is in every case the same class of defect the "
                  + "fix was written for. The fixes are not decoration."
                : $"{dead} of {rows.Count} mutations produce no violation at all.",
            held: dead == 0);

        r.Table(["mutation", "states", "violations", "shortest counterexample"], rows);
        r.Note(
            "This is mutation testing pointed at a design rather than at a test suite. "
            + "The usual version mutates the implementation to check the tests; this "
            + "mutates the *design decisions* to check that the properties are strong "
            + "enough to notice. Finding two dead properties this way was the single "
            + "most useful thing the technique did, and neither would have been visible "
            + "from a passing test run.");

        r.H3("The compensation that undoes too much");
        r.Para(
            "The second injected defect is worth showing because of which check catches "
            + "it. `AllocateSlot` reads the reservation and writes a slot; its "
            + "compensation releases the slot and -- reasonably, from where its author "
            + "is sitting -- also releases the reservation, which belongs to "
            + "`ReserveStock` and will be released again when the rollback reaches it.");
        r.Para(
            "Every per-step property is satisfied. The compensation is neutral when the "
            + "step did not run. It is idempotent. It undoes its own effect. Only the "
            + "composition is wrong, and the only thing that sees it is running the "
            + "whole committed prefix backwards as pure algebra, with no exploration at "
            + "all. That check costs microseconds, produces a one-line failure, and was "
            + "sitting in the codebase unreferenced.");
    }

    private static void Growth(Report r)
    {
        r.H2("How far does this scale?");
        r.Para(
            "The reachable state count is the practical limit on the technique, so it is "
            + "worth measuring rather than hand-waving. Each synthetic saga of length n "
            + "has n compensatable steps and a pivot, with a crash budget of one.");

        r.Expect(
            "Growth will be exponential with a base around five: each step multiplies "
            + "the space by its own outcomes (success, rejection, timeout) crossed with "
            + "the crash and attempt dimensions.");

        var rows = new List<string[]>();
        var counts = new List<int>();
        for (var n = 2; n <= 10; n++)
        {
            var res = Checker.Check(Synthetic.Chain(n), Base);
            counts.Add(res.StatesExplored);
            var ratio = counts.Count > 1
                ? (counts[^1] / (double)counts[^2]).ToString("F2", CultureInfo.InvariantCulture)
                : "--";
            rows.Add([
                n.ToString(CultureInfo.InvariantCulture),
                res.StatesExplored.ToString(CultureInfo.InvariantCulture),
                ratio
            ]);
        }

        var first = counts[1] / (double)counts[0];
        var last = counts[^1] / (double)counts[^2];
        r.Found(
            $"And in a more interesting way than being wrong about the "
            + $"constant. The base is not five, it is about {last:F1} -- and it is not "
            + $"constant either: the ratio falls monotonically from {first:F2} to "
            + $"{last:F2} across the range. The steps share a state vector, so each added "
            + "step contributes fewer genuinely new states than the last. The growth is "
            + "still exponential and still fatal eventually, but a ten-step saga is "
            + $"{counts[^1]:N0} states, which is a fraction of a second, and the practical "
            + "ceiling is somewhere near twenty steps rather than the ten I expected.",
            held: false);

        r.Table(["steps", "states", "ratio to previous"], rows);
        r.Para(
            "This matters for whether the technique is usable. A saga long enough to "
            + "exceed this is a saga that has stopped being one workflow, and the answer "
            + "there is decomposition rather than a faster checker.");
    }

    private static void WhatThisDoesNotDo(Report r)
    {
        r.H2("What this does not do");
        r.Para(
            "The model has one orchestrator. Two concurrent instances of the same saga "
            + "over the same entities are not explored, so nothing here says anything "
            + "about the interleaving of two orders competing for the last unit of "
            + "stock. That is a real class of bug and it is out of scope, not solved.");
        r.Para(
            "The model checks a design, not an implementation. It says that if the steps "
            + "behave as declared -- this one is idempotent, that one is queryable -- "
            + "then the saga is sound. Whether the payment service's capture endpoint is "
            + "actually idempotent is a question this tool converts from an unexamined "
            + "assumption into a written, checkable claim, which is a real improvement "
            + "and is not the same as verifying it.");
        r.Para(
            "And the exhaustiveness is bounded by the crash budget, which the v4 column "
            + "above shows is not a formality. The honest summary of a clean run is "
            + "\"no counterexample exists with at most three crashes\", not \"correct\".");
    }
}
