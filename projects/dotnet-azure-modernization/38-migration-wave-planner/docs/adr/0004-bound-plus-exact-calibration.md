# 4. A bound and an exact calibration, not a bound alone

**Status:** accepted

## Context

The annealer returns £2,967,337 and calls it the best plan found. That is a
claim about a search, not about the problem. Two things could be true:

1. the plan is near-optimal and the remaining gap is unrecoverable;
2. the search is bad and a better planner would find a much cheaper plan.

A stakeholder cannot distinguish these, and neither can the annealer.

## Decision

Do both halves.

**A lower bound** (`wave/bounds.py`) that no valid plan can beat, built from
five terms that are each provably unavoidable: minimum wave count from
capacity, prefix cutover bounds on on-premises run cost, the dual-run overlap,
the hybrid links that must exist under any plan, and remediation. Gap against
the bound: 15.91%.

**Exact optima on sub-instances** small enough to enumerate. `connected_slice`
takes a connected sub-estate of `n` units, `restrict` rebuilds a consistent
estate around it, and `exact_optimum` enumerates every valid plan. Run for
`n = 6..9`.

## Consequences

**The two halves answer different questions and neither is sufficient alone.**

The bound alone says "within 15.91% of optimal". That is technically an upper
bound on the loss and practically useless, because the bound's own slack is
unknown — 15.91% could be all search failure or all bound looseness.

The exact calibration says: on every sub-instance that can be checked, the
annealer's gap to the true optimum is **0.000%**, while the bound is loose by
5.94–6.47%. So most of the headline gap is bound slack.

**The calibration also contradicted its own prediction, and I reported that.**
The written prediction was that the annealer would show a few percent of true
gap on small instances. It showed zero. But so did the best naive heuristic —
the sub-instances are too easy to separate the planners. That is a limitation
of the calibration rather than a result about the annealer, and the report
says so in a note directly under the table. Reporting the calibration without
that caveat would have been the more flattering and less true option: "our
heuristic is provably optimal on every instance we could check" reads well
until someone notices the instances could not tell any heuristic apart.

**One piece of the gap is provably unrecoverable.** The bound charges nothing
for licence waste; the best plan pays £63,382 of it, which is 15.6% of the
gap. No search improvement can recover that, because it is a real cost the
bound simply omits. Decomposing the gap this way is what turns "15.91%" from a
number into a statement.

**Why the calibration stops at 9 units.** The enumerated space grows by about
5.5x per unit added, so the full 19-unit instance is roughly `10^7` times
larger than the largest solved exactly. That statement is machine-independent,
which matters — see below.

## What this cost

**Wall-clock timings had to come out of the report.** The first version of the
calibration table had an "exact time" column. It made `docs/results.md`
non-reproducible: the byte-comparison check in
`tests/test_results_integrity.py` failed on the second run with a diff of
`6.3s` against `6.1s`. The fix was to delete the column and replace it with
the growth-factor extrapolation above, which is both reproducible and a better
argument, because it does not depend on the machine it was measured on.

**`restrict` is a real piece of work.** Slicing an estate is not filtering a
list: the sub-estate needs consistent team rates, wave capacities, licence
renewals and dependency sets, and it must satisfy the same thirteen invariants
as the full one or the sub-instance is not a valid instance of the same
problem. That code exists only to support the calibration, and it is about a
fifth of `bounds.py`.

## Alternatives considered

**MILP with a commercial solver.** Would give exact answers at full size and
make this ADR unnecessary. Rejected: it makes the project a demonstration of a
solver rather than of the modelling, and the interesting content here — that
the plan does not exist, that the renewal calendar matters, that freezes are
not a hedge — is all upstream of the optimisation.

**Bound only.** Rejected: it is the standard move and it hides the question it
appears to answer. A gap of 15.91% against an uncalibrated bound is not
evidence about the search.

**Exact only, on a smaller estate.** Rejected: shrinking the estate to
something exactly solvable removes the structure that generates every finding
in the report. The estate is the point.
