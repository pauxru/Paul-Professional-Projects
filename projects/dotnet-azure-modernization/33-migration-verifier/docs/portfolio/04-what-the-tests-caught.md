# 4. What the tests caught

Five defects the test suite found in this project's own code. All of them are the same species as
the defects the project is *about*: no exception, no crash, no failing assertion anywhere -- just
a number in the report that was quietly wrong and looked exactly like a finding.

I am writing them down because a portfolio project that only shows the finished artifact is
hiding the part that took the time.

## 1. A rule that silently disabled another rule

**Symptom:** removing a canonicalisation rule sometimes *increased* the number of reported
differences.

That should be impossible. Rules only ever make the verifier quieter; removing one should
weakly increase the difference count, never the reverse. When it went the other way, something
was wrong with the machinery rather than with the data.

**Cause:** `Rule.NUMERIC` dispatched on the runtime type of the value:

```java
if (v instanceof Number n) return new BigDecimal(n.toString()).stripTrailingZeros();
```

SQLite returns timestamps as `Long`. A `Long` is a `Number`. So `NUMERIC` consumed the epoch
milliseconds and handed `TEMPORAL` a `BigDecimal`, which `TEMPORAL` did not recognise and passed
through untouched.

**Effect:** whenever `NUMERIC` was enabled, `TEMPORAL` did nothing. The rule-subset lattice --
the central measurement of the project, 128 subsets -- was computed against a `TEMPORAL` that
only functioned in the 64 subsets where `NUMERIC` was absent. Nothing threw. No test failed. The
verifier simply reported more differences, and every one of them looked like a real finding.

**Fix:** rules are scoped to columns, not to value types (ADR 003). `NUMERIC` governs `amount`;
`TEMPORAL` governs `seen`; they can no longer see each other's data.

**The lesson is the project's own thesis pointed inward.** A component that suppresses noise,
silently consuming the input another component needed, producing output that is wrong in a way
indistinguishable from a genuine result. A verifier whose rules interfere with each other has
the same epistemic problem as the migration it is verifying -- and it is harder to notice,
because there is no second copy of the verifier to compare against.

## 2. A positive control that was always positive

**Symptom:** every verifier configuration scored 5 of 5 on the cutover gate, including
configurations the blindfold matrix had already proven substantially blind. The gate said `GO`
where section 6 said "cannot see".

**Cause:** the gate credited a control as caught whenever the verifier reported *anything*:

```java
if (!comparison.differences(source, target).isEmpty()) caught++;   // wrong
```

Two rows in the corpus are corrupted by the engine pair itself, under any migrator including the
faithful one. So the verifier was never silent. It always had those two differences available,
and the gate credited them to whichever control was running.

**Effect:** the gate measured its own baseline and called it sensitivity. Its output was
meaningless, and — worse — meaningless in the reassuring direction.

**Fix:** a control is caught only if the verifier flags a row *that this control damaged*
(ADR 005). And a control that damages nothing in the corpus is dropped from the denominator
rather than counted as missed, so the corpus's coverage gaps do not get reported as the
verifier's blindness.

**What changed:** the row-count verifier went from 5/5 to 0/5 -- it compares two integers and
cannot detect a value-level defect by construction. And the full rule set went from passing to
`NO_GO_BLIND` at 3/5, which inverted section 9's conclusion into a much stronger one: *the
configuration with perfect precision is the one that fails its controls.*

A positive control that is positive whatever you do is not a control.

## 3. Staleness that was actually type coercion

**Symptom:** `Backfill.stale()` reported a row permanently stale no matter how the backfill ran
-- including with no concurrent writes at all.

**Cause:** `stale()` compared the current source value against the current target value. For the
huge-amount row, those two never agree, because the engine pair destroys the value in transit.
The row was not stale. It was corrupted, which is a different section of the report.

**Effect:** the backfill experiment conflated two mechanisms and would have reported a nonzero
staleness floor that had nothing to do with concurrency.

**Fix:** record what was actually copied, at copy time, in a `Map<Long, Object> copiedValue`, and
define staleness as *source-now differs from the value this backfill wrote*. Corruption is then
somebody else's measurement.

**The general shape:** two independent failure mechanisms, one metric. The metric was
well-defined and meaningless. Section 7's numbers are only interpretable because the two are
separated.

## 4. A test whose assumption was backwards

**Symptom:** `batchSizeChangesWhichRowsGoStaleButNotThatSomeDo` failed at batch size 1.

**Cause:** the code was right and I was wrong. I had assumed the staleness window was
irreducible -- that any backfill racing concurrent writes leaves something stale regardless of
batching. At batch size 1 the window closes entirely for this write schedule: each write lands
before the single row it targets is copied.

**Fix:** the test now asserts the true relationship -- staleness *grows* with batch size, and one
big batch is the worst case -- which is a more useful statement than the one I set out to make,
and it is the operational advice section 7 actually gives.

**Worth naming separately** because it is the only one of the four where the failing test was
correct and the belief was wrong. The temptation to "fix" the code to match the assumption was
real, and the reason I did not is that the failure had a mechanism I could explain. A test
failure you can explain is information; a test failure you route around is a deleted experiment.

## 5. A coin flip in the report generator

**Symptom:** `theCommittedReportMatchesAFreshRun` failed about half the time, and only when the
whole suite ran. In isolation it always passed.

```
committed: | `trim` | `[name, code]` | **not injective** | declares two different values equal |
fresh:     | `trim` | `[code, name]` | **not injective** | declares two different values equal |
```

**Cause:** `Rule` stored its governed columns in `java.util.Set.of(columns)`.
`Set.of` is not merely unordered -- since JDK 9 it deliberately *randomises* iteration order per
JVM, using a `SALT` derived from `System.nanoTime()` at class-initialisation time. The intent is
to stop callers depending on an order that is not part of the contract. It works: this project
depended on it, and got caught.

**Why it took so long to see.** The determinism stage of `test.ps1` runs the generator in three
separate JVMs and hashes the output. It passed. Three coin flips landing the same way is a 1-in-4
event with two elements, and the stage had been passing for a while. What actually caught it was
the *freshness* test running inside the surefire JVM, where a different class-loading order
changes `nanoTime` at the moment `ImmutableCollections` initialises.

So the check that found it was not the one designed to find it. That is worth sitting with: the
determinism stage tests exactly this property and was not sensitive enough, while a test written
for a different purpose was.

**Fix:** an insertion-ordered `LinkedHashSet`, plus two regression tests -- one asserting the
concrete order, one hammering it 50 times.

**The general rule this earns:** any `Set.of` or `Map.of` whose iteration order can reach a
rendered artifact is a latent coin flip. It will not show up in review, it will not show up in a
single test run, and when it does show up it looks like a flaky test rather than a bug. The
project's whole subject is checks whose silence is not evidence, and here was one in its own
build.


None of them produced an exception. All produced *numbers* -- or, in the last case, a number half the time, and the numbers went into a
report, and the report was wrong in ways that read as findings.

Three of the five were caught by an invariant rather than by an expected value:

- removing a rule must not increase differences (monotonicity)
- a control must be caught by its own damage (attribution)
- staleness must be zero when nothing is concurrent (a boundary case)

The fourth was caught by a boundary case disagreeing with a belief. The fifth was caught by a test written for something else entirely, which is the least satisfying and most realistic entry on the list.

That is the argument for this style of testing on measurement code. Asserting expected outputs
on a system whose outputs you are trying to discover is circular -- you encode the result you
expect and the test agrees with you. Asserting structural invariants that must hold *whatever*
the result is will catch you when the apparatus is broken, which is the failure mode that
actually matters, because a broken apparatus produces publishable-looking numbers.
