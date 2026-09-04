# ADR 0005: Record predictions before measuring, and keep the ones that fail

## Status

Accepted.

## Context

A document full of measurements that all confirm the author's expectations is
either a lucky author or an edited document, and a reader has no way to tell
which. This matters more than it sounds: the entire value of `docs/results.md`
is that a reader can believe its numbers without rerunning it, and that belief
has to be earned by something structural rather than by tone.

The specific hazard is not fraud. It is the ordinary, almost invisible drift
where you run an experiment, get a number you did not expect, think about it,
realise the number is obviously right, and write it up as though you had
expected it all along. Every step of that is honest. The result is a document
that has lost the information about which results were surprising — which is
the most useful information in it.

## Decision

The report DSL enforces the order. `Section.Expect(...)` records a prediction;
`Section.Found(held bool, ...)` records what happened and adjudicates. The
predicate passed to `Found` is evaluated from measured data, so the HELD or
CONTRADICTED label is computed rather than typed.

`Report.Section` panics if a new section is opened while the previous one has
an open prediction with no verdict. `TestEveryPredictionIsAdjudicated` checks
the same invariant from the outside.

Contradicted predictions stay in the document, at the same prominence as the
ones that held, with an explanation of why the prediction was wrong.

## Consequences

**The summary table is a scoreboard.** Fourteen predictions, thirteen held, one
contradicted. A reader can see at a glance how much of this document is
confirmation and how much is discovery.

**The contradictions are the best sections.** Three examples, all of which
would have been quietly deleted under a looser process:

*Section 2* predicted that removing the long-running read would drop
amplification below 10×. It measured 132×. Working out why produced the actual
model: the long read decides how long the DDL waits, the arrival rate decides
how many queries pile up once it does, and the two are independent. "We checked,
nothing long-running is on that table" is a mitigation worth 83×, not a safety
guarantee. That distinction is the most useful sentence in the report and it
exists only because the prediction was wrong.

*Section 3* predicted blocked time would be superlinear in request count. It is
linear in arrival rate (×1.995) and quadratic in stall duration (×4.202). The
corrected law is sharper than the guess and is the direct quantitative argument
for `lock_timeout`: halving the stall quarters the damage, while halving the
traffic only halves it.

*Section 11* predicted the proportional controller would be smoother than AIMD
and riskier. It measured a coefficient of variation of 0.731 against AIMD's
0.207 — it is the *noisiest* of the three. The mechanism is that multiplying
batch size by a lag-error term makes the error drive the *derivative* of size,
which is integral action on a delayed plant, which is a limit cycle. That
finding prompted the AIAD ablation in §12, which isolates the multiplicative
decrease as the component that actually damps. An entire section exists because
one prediction was wrong.

**There is a standing temptation to reword rather than understand**, and
`TestContradictionsSurvive` exists to resist it. If the contradiction count
ever reaches zero, the test fails and asks the author to delete it in the same
commit, with a reason. That is deliberately annoying. It is annoying in exactly
the moment when annoyance is useful.

**Predictions must be falsifiable.** "Amplification is high" is unfalsifiable
and worthless. "Amplification exceeds 100×" can be wrong. Every `Expect` in the
report states a threshold or an ordering, and the `Found` predicate is a
comparison against measured values.

**This costs something in polish.** The report admits, in public, that the
author was wrong about the shape of the scaling law and about which controller
was noisy. A document that hid those would read as more authoritative. It would
also be worth less, because the reader could not distinguish it from one whose
author had guessed right by accident.

## Alternatives considered

**Write predictions in comments and adjudicate by hand.** Rejected: hand
adjudication is exactly the step that drifts, and a comment is not checked by
anything.

**Keep contradictions in a separate "things I got wrong" appendix.** Rejected:
it sorts the surprising results away from the context that makes them
meaningful, and an appendix is where things go to not be read.

**Record predictions in git history — write them, commit, then measure.**
Genuinely stronger, since the timestamps are independent evidence. Rejected as
too fragile a process to rely on across dozens of sections, and it does not
survive a rebase. The DSL enforces the ordering structurally, in the code, on
every run, which is weaker evidence but a stronger guarantee.
