using System.Diagnostics;

namespace Sagas.Core;

public enum PropertyKind
{
    /// <summary>An execution that can neither finish nor roll back.</summary>
    StuckState,

    /// <summary>Rolled back, but the world did not come back to where it started.</summary>
    DirtyAbort,

    /// <summary>A declared business invariant was false in a reachable state.</summary>
    InvariantViolated,

    /// <summary>A step declared idempotent has a reachable state where applying it twice differs from once.</summary>
    DeclaredIdempotenceIsFalse,

    /// <summary>
    /// A compensation changed the state of a step that had not run. Because the
    /// orchestrator cannot tell a lost acknowledgement from a lost request, this
    /// is not an edge case: it is the normal case.
    /// </summary>
    CompensationNotNeutral,

    /// <summary>Structural: the compensatable / pivot / retriable ordering rule is broken.</summary>
    ShapeViolation,

    /// <summary>Algebraic: undoing a committed prefix in reverse order does not restore the start state.</summary>
    ReverseOrderDoesNotRecover
}

public sealed record TraceStep(string Label, Config To);

public sealed record Violation(PropertyKind Kind, string Detail, IReadOnlyList<TraceStep> Trace)
{
    public int Depth => Trace.Count;

    public string Render(WorldState initial)
    {
        var lines = new List<string> { $"{Kind}: {Detail}", $"    start   {initial}" };
        foreach (var t in Trace)
        {
            lines.Add($"    {t.Label,-46} {t.To}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}

public sealed record CheckResult
{
    public required Saga Saga { get; init; }

    public required CheckerOptions Options { get; init; }

    public required IReadOnlyList<Violation> Violations { get; init; }

    public required int StatesExplored { get; init; }

    public required int Transitions { get; init; }

    public required int MaxDepth { get; init; }

    /// <summary>
    /// False when <see cref="CheckerOptions.MaxStates"/> stopped the search. A
    /// result with this false proves nothing, and every consumer in this codebase
    /// treats it as a failure rather than as a pass.
    /// </summary>
    public required bool Exhaustive { get; init; }

    public required IReadOnlyDictionary<Phase, int> Terminals { get; init; }

    public required TimeSpan Elapsed { get; init; }

    public bool Safe => Exhaustive && Violations.Count == 0;

    /// <summary>Whether any explored execution used its full retry budget. See the note in the report on bound sufficiency.</summary>
    public required bool RetryBoundReached { get; init; }

    public IEnumerable<Violation> Of(PropertyKind kind) => Violations.Where(v => v.Kind == kind);

    public Violation? Shortest(PropertyKind kind) =>
        Of(kind).OrderBy(v => v.Depth).ThenBy(v => v.Detail, StringComparer.Ordinal).FirstOrDefault();
}

public static class Checker
{
    /// <summary>
    /// Breadth-first, so the first counterexample found for any property is a
    /// shortest one. That matters more than it sounds: a fifteen-transition trace
    /// is something an engineer nods at and files; a four-transition trace is
    /// something they fix that afternoon.
    /// </summary>
    public static CheckResult Check(Saga saga, CheckerOptions? options = null)
    {
        var opts = options ?? new CheckerOptions();
        var sw = Stopwatch.StartNew();
        var steps = saga.Steps;

        var start = new Config(saga.Initial, Phase.Forward, 0, 0, 0, 0);
        var parents = new Dictionary<Config, (Config Parent, string Label)>();
        var depth = new Dictionary<Config, int> { [start] = 0 };
        var queue = new Queue<Config>();
        queue.Enqueue(start);

        var violations = new List<Violation>();

        // Violations that are true of the design itself rather than of any
        // reachable execution. They carry no trace because there is nothing to
        // trace: a misshapen saga is wrong before it runs.
        var staticViolations = saga.ShapeViolations()
            .Select(s => new Violation(PropertyKind.ShapeViolation, s, []))
            .ToList();

        var seenViolation = new HashSet<(PropertyKind, string)>();
        var terminals = new Dictionary<Phase, int>();
        var transitions = 0;
        var maxDepth = 0;
        var retryBoundReached = false;
        var exhaustive = true;

        void Report(PropertyKind kind, string detail, Config at)
        {
            // One counterexample per distinct problem. Ten thousand traces to the
            // same broken compensation is noise that hides the second bug.
            if (seenViolation.Add((kind, detail)))
            {
                violations.Add(new Violation(kind, detail, TraceTo(at, start, parents)));
            }
        }

        // Algebraic pre-checks, before any exploration. These are facts about the
        // design that hold or fail without reference to a reachable state, so
        // they carry no trace and cost nothing.
        for (var k = 0; k < steps.Count; k++)
        {
            var algebra = ReverseOrderRecovers(saga, k);
            if (algebra is not null)
            {
                staticViolations.Add(algebra);
            }
        }

        while (queue.Count > 0)
        {
            if (depth.Count > opts.MaxStates)
            {
                exhaustive = false;
                break;
            }

            var c = queue.Dequeue();
            var d = depth[c];
            maxDepth = Math.Max(maxDepth, d);

            foreach (var inv in saga.Invariants)
            {
                if (!inv.Holds(c.State))
                {
                    Report(PropertyKind.InvariantViolated, inv.Name, c);
                }
            }

            if (c.IsTerminal)
            {
                terminals[c.Phase] = terminals.GetValueOrDefault(c.Phase) + 1;

                switch (c.Phase)
                {
                    case Phase.Stuck:
                        // The label on the edge that got here carries the reason.
                        Report(PropertyKind.StuckState,
                            parents.TryGetValue(c, out var p) ? p.Label : "unreachable-by-construction", c);
                        break;
                    case Phase.Aborted:
                    {
                        var residue = Residue(saga, c.State);
                        if (residue is not null)
                        {
                            Report(PropertyKind.DirtyAbort, residue, c);
                        }

                        break;
                    }
                }

                continue;
            }

            CheckAlgebraAt(saga, c, Report);

            if (c.Phase == Phase.Forward && c.Cursor < steps.Count &&
                c.Attempts + 1 >= steps[c.Cursor].MaxAttempts)
            {
                retryBoundReached = true;
            }

            foreach (var (next, label) in Successors(saga, c, opts))
            {
                transitions++;
                if (depth.ContainsKey(next))
                {
                    continue;
                }

                depth[next] = d + 1;
                parents[next] = (c, label);
                queue.Enqueue(next);
            }
        }

        sw.Stop();

        return new CheckResult
        {
            Saga = saga,
            Options = opts,
            Violations = [.. staticViolations, .. violations],
            StatesExplored = depth.Count,
            Transitions = transitions,
            MaxDepth = maxDepth,
            Exhaustive = exhaustive,
            Terminals = terminals,
            Elapsed = sw.Elapsed,
            RetryBoundReached = retryBoundReached
        };
    }

    /// <summary>
    /// The two properties that are facts about a state rather than about a path,
    /// checked wherever the path happens to reach.
    /// </summary>
    private static void CheckAlgebraAt(Saga saga, Config c, Action<PropertyKind, string, Config> report)
    {
        var steps = saga.Steps;

        if (c.Phase == Phase.Forward && c.Cursor < steps.Count)
        {
            var step = steps[c.Cursor];
            if (step.Idempotent)
            {
                var once = step.Forward(c.State);
                if (!step.Forward(once).Equals(once))
                {
                    report(PropertyKind.DeclaredIdempotenceIsFalse,
                        $"'{step.Name}' is declared idempotent, but applying it twice from a reachable state gives " +
                        $"{step.Forward(once).DiffFrom(once)}",
                        c);
                }
            }
        }

        if (c.Phase == Phase.Compensating && c.Cursor >= 0 && c.Cursor < steps.Count)
        {
            var step = steps[c.Cursor];
            if (step.Compensate is null || Landed(c, c.Cursor))
            {
                return;
            }

            // We are about to compensate a step whose effect is not present. The
            // orchestrator got here honestly: a timeout does not say which of the
            // request and the reply was lost. If the compensation is not the
            // identity here, it is inventing an effect.
            var after = step.Compensate(c.State);
            if (!after.Equals(c.State))
            {
                report(PropertyKind.CompensationNotNeutral,
                    $"compensating '{step.Name}' when its effect is not present changes {after.DiffFrom(c.State)}",
                    c);
            }
        }
    }

    private static bool Landed(Config c, int i) => c.LandedCount(i) > 0;

    private static string? Residue(Saga saga, WorldState end)
    {
        var names = saga.Schema.Names;
        var dirty = new List<string>();
        for (var i = 0; i < names.Length; i++)
        {
            if (end.At(i) != saga.Initial.At(i) && !saga.AcceptableResidue.Contains(names[i]))
            {
                dirty.Add($"{names[i]} {saga.Initial.At(i)} -> {end.At(i)}");
            }
        }

        return dirty.Count == 0 ? null : "rolled back but left " + string.Join(", ", dirty);
    }

    private static IEnumerable<(Config Next, string Label)> Successors(Saga saga, Config c, CheckerOptions opts)
    {
        var steps = saga.Steps;

        if (c.Phase == Phase.Forward)
        {
            if (c.Cursor >= steps.Count)
            {
                yield return (c with { Phase = Phase.Done }, "all steps committed");
                yield break;
            }

            var i = c.Cursor;
            var step = steps[i];
            var after = step.Forward(c.State);
            var landedAfter = c.Landing(i);
            var lastAttempt = c.Attempts + 1 >= step.MaxAttempts;
            var rollbackFrom = opts.Scope == CompensationScope.IncludeUncertainStep ? i : i - 1;

            yield return (
                new Config(after, Phase.Forward, i + 1, 0, c.Crashes, landedAfter),
                $"ok({step.Name})");

            // The two timeouts. The orchestrator cannot tell them apart, which is
            // why they must both be explored from the same observation.
            if (!lastAttempt)
            {
                yield return (
                    new Config(after, Phase.Forward, i, c.Attempts + 1, c.Crashes, landedAfter),
                    $"timeout({step.Name}) [applied] -- retry");
                yield return (
                    new Config(c.State, Phase.Forward, i, c.Attempts + 1, c.Crashes, c.Landed),
                    $"timeout({step.Name}) [lost] -- retry");
            }
            else if (step.Queryable)
            {
                // The status query resolves the ambiguity, so the orchestrator can
                // pick the correct direction instead of guessing one. It reports
                // the truth about the *step*, not about this attempt -- which
                // matters when an earlier attempt or a pre-crash call already
                // committed.
                yield return (
                    new Config(after, Phase.Forward, i + 1, 0, c.Crashes, landedAfter),
                    $"timeout({step.Name}) -- status query says committed, roll forward");

                yield return c.LandedCount(i) > 0
                    ? (new Config(c.State, Phase.Forward, i + 1, 0, c.Crashes, c.Landed),
                        $"timeout({step.Name}) -- status query says an earlier attempt committed, roll forward")
                    : (new Config(c.State, Phase.Compensating, i - 1, 0, c.Crashes, c.Landed),
                        $"timeout({step.Name}) -- status query says nothing happened, roll back");
            }
            else if (step.Kind != StepKind.Retriable)
            {
                yield return (
                    new Config(after, Phase.Compensating, rollbackFrom, 0, c.Crashes, landedAfter),
                    $"timeout({step.Name}) [applied] -- give up, roll back");
                yield return (
                    new Config(c.State, Phase.Compensating, rollbackFrom, 0, c.Crashes, c.Landed),
                    $"timeout({step.Name}) [lost] -- give up, roll back");
            }

            // A retriable step at its attempt cap generates only the succeeding
            // transition above. That is the definition of retriable being taken
            // seriously: if the step can give up, it was never retriable, and
            // ShapeViolations already says so.

            if (step.CanBeRejected && !(step.Queryable && c.LandedCount(i) > 0))
            {
                // A definitive "no" is authoritative about *this attempt* and says
                // nothing about attempt one, which may have landed and then lost
                // its acknowledgement. Rolling back from i-1 on that basis strands
                // the first effect -- the depth-3 counterexample in section 3.
                //
                // Two things justify skipping step i: a status query (ground
                // truth), or knowing no call was ever made. The second is only
                // knowable if the orchestrator journalled its intent before
                // calling, which is what JournalAttempts models.
                var knowsNothingLanded = step.Queryable || (opts.JournalAttempts && c.Attempts == 0);
                var from = knowsNothingLanded ? i - 1 : rollbackFrom;

                yield return step.Kind == StepKind.Retriable
                    ? (new Config(c.State, Phase.Stuck, i, 0, c.Crashes, c.Landed),
                        $"'{step.Name}' is declared retriable but refused, and the pivot is already committed")
                    : (new Config(c.State, Phase.Compensating, from, 0, c.Crashes, c.Landed),
                        $"rejected({step.Name})");
            }

            if (c.Crashes < opts.CrashBudget)
            {
                // Intent journalled before the call means a recovered orchestrator
                // knows an attempt was in flight, even if it knows nothing else.
                // Without it, recovery cannot distinguish "never called" from
                // "called, outcome unknown" -- and then attempts == 0 stops being
                // evidence of anything.
                var a = opts.JournalAttempts ? Math.Max(c.Attempts, 1) : 0;
                yield return (
                    new Config(c.State, Phase.Forward, i, a, c.Crashes + 1, c.Landed),
                    $"crash before {step.Name}");
                yield return (
                    new Config(after, Phase.Forward, i, a, c.Crashes + 1, landedAfter),
                    $"crash after {step.Name}");
            }

            yield break;
        }

        if (c.Phase != Phase.Compensating)
        {
            yield break;
        }

        if (c.Cursor < 0)
        {
            yield return (c with { Phase = Phase.Aborted }, "rollback complete");
            yield break;
        }

        var j = c.Cursor;
        var cstep = steps[j];

        if (cstep.Compensate is null)
        {
            yield return (
                new Config(c.State, Phase.Stuck, j, 0, c.Crashes, c.Landed),
                $"rollback reached '{cstep.Name}', which is {cstep.Kind.ToString().ToLowerInvariant()} and has no inverse");
            yield break;
        }

        var undone = cstep.Compensate(c.State);
        var landedCleared = c.Undoing(j);
        var atCap = c.Attempts + 1 >= opts.CompensationAttempts;

        yield return (
            new Config(undone, Phase.Compensating, j - 1, 0, c.Crashes, landedCleared),
            $"compensated({cstep.Name})");

        if (!atCap)
        {
            yield return (
                new Config(undone, Phase.Compensating, j, c.Attempts + 1, c.Crashes, landedCleared),
                $"comp-timeout({cstep.Name}) [applied] -- retry");
            yield return (
                new Config(c.State, Phase.Compensating, j, c.Attempts + 1, c.Crashes, c.Landed),
                $"comp-timeout({cstep.Name}) [lost] -- retry");
        }
        else if (!opts.CompensationRetriesForever)
        {
            yield return (
                new Config(c.State, Phase.Stuck, j, 0, c.Crashes, c.Landed),
                $"compensation for '{cstep.Name}' ran out of retries");
        }

        // A definitive refusal is not a timeout. Retrying it forever does not
        // help, so the fairness assumption above does not apply. Either the saga
        // has declared what happens instead, or this is where it stops.
        if (cstep.CompensationCanBeRejected)
        {
            if (cstep.CompensationFallback is not null)
            {
                yield return (
                    new Config(cstep.CompensationFallback(c.State), Phase.Compensating, j - 1, 0, c.Crashes,
                        landedCleared),
                    $"compensation for '{cstep.Name}' refused; declared fallback applied");
            }
            else
            {
                yield return (
                    new Config(c.State, Phase.Stuck, j, 0, c.Crashes, c.Landed),
                    $"compensation for '{cstep.Name}' was definitively rejected and no fallback is declared");
            }
        }

        if (c.Crashes < opts.CrashBudget)
        {
            var a = opts.JournalAttempts ? Math.Max(c.Attempts, 1) : 0;
            yield return (
                new Config(c.State, Phase.Compensating, j, a, c.Crashes + 1, c.Landed),
                $"crash before compensating {cstep.Name}");
            yield return (
                new Config(undone, Phase.Compensating, j, a, c.Crashes + 1, landedCleared),
                $"crash after compensating {cstep.Name}");
        }
    }

    private static List<TraceStep> TraceTo(
        Config target,
        Config start,
        Dictionary<Config, (Config Parent, string Label)> parents)
    {
        var reversed = new List<TraceStep>();
        var cur = target;
        var guard = 0;
        while (!cur.Equals(start))
        {
            if (!parents.TryGetValue(cur, out var p) || ++guard > 10_000)
            {
                break;
            }

            reversed.Add(new TraceStep(p.Label, cur));
            cur = p.Parent;
        }

        reversed.Reverse();
        return reversed;
    }

    /// <summary>
    /// Undo a committed prefix in reverse order and see whether the world comes
    /// back. Pure algebra over one path -- no exploration -- which makes it a
    /// cheap first filter: a saga that fails this cannot possibly pass the model
    /// check, and the failure is far easier to read.
    /// </summary>
    public static Violation? ReverseOrderRecovers(Saga saga, int throughStep)
    {
        var s = saga.Initial;
        var applied = new List<int>();
        for (var i = 0; i <= throughStep && i < saga.Steps.Count; i++)
        {
            if (saga.Steps[i].Compensate is null)
            {
                break;
            }

            s = saga.Steps[i].Forward(s);
            applied.Add(i);
        }

        for (var k = applied.Count - 1; k >= 0; k--)
        {
            s = saga.Steps[applied[k]].Compensate!(s);
        }

        if (UndeclaredResidue(saga, s).Count == 0)
        {
            return null;
        }

        var names = string.Join(" -> ", applied.Select(i => saga.Steps[i].Name));
        return new Violation(
            PropertyKind.ReverseOrderDoesNotRecover,
            $"committing [{names}] then undoing in reverse leaves {s.DiffFrom(saga.Initial)}",
            []);
    }

    /// <summary>
    /// Variables that differ from the initial state and were not declared as
    /// acceptable residue. This is the algebraic check's view of "clean"; the
    /// dirty-abort property has its own string-returning form above.
    /// </summary>
    private static List<string> UndeclaredResidue(Saga saga, WorldState s)
    {
        var dirty = new List<string>();
        for (var i = 0; i < saga.Schema.Count; i++)
        {
            var name = saga.Schema.Names[i];
            if (s.At(i) != saga.Initial.At(i) && !saga.AcceptableResidue.Contains(name))
            {
                dirty.Add(name);
            }
        }

        return dirty;
    }
}
