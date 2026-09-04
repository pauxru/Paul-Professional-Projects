# ADR 002: Per-replica clocks, not a global clock

**Status.** Accepted. Supersedes the original event loop.

## Context

A multi-replica gateway has several independent engines. Each runs its own
decode step, and the steps take different amounts of time because the batches
differ in size.

The first event loop kept a single global clock. Each iteration stepped every
replica, collected the elapsed time each reported, and advanced the clock by the
minimum of those values.

## Decision

Each replica carries its own clock. The gateway advances to the earliest
next-event time across all replicas plus any scheduler backoff deadline,
processes whatever becomes ready at that instant, and repeats.

## Consequences

**The original design was wrong in a way that produced believable numbers.**
Advancing the global clock by the minimum elapsed time credits every slower
replica with work it had not done. A replica that took 25ms to run a large batch
was recorded as having spent 12ms because another replica finished a small batch
faster. Single-replica runs were unaffected — with one replica, the minimum is
the only value — so the bug was invisible in every test that did not involve a
fleet.

Little's Law caught it. See
[`../portfolio/04-bugs-the-simulator-found.md`](../portfolio/04-bugs-the-simulator-found.md#1-lockstep-replica-clocks).

**Occupancy must be integrated at every clock boundary.** Once clocks are
per-replica, the in-system count changes at instants that are not global events.
The integral needs a sample at *every* replica-clock boundary greater than the
current time — including the boundary belonging to a replica that has just
emptied. Omitting those over-counts occupancy and breaks Little's Law again, for
a bookkeeping reason with no physical meaning. The two failures are
indistinguishable from the outside, which is why `next_event_us` treats an empty
replica's clock as an event rather than skipping it.

**The loop is no longer a fixed-step simulation.** There is no tick. Nothing
happens between events, so nothing needs to be computed between events, and the
cost of a run is proportional to the number of state changes rather than to
simulated time. Runs at low utilisation are correspondingly fast.

**Cost.** Finding the next event is O(replicas) per iteration. With single-digit
fleet sizes a linear scan beats a heap, and it is much easier to read.

**Pinned by.** `sim_invariants.rs::littles_law_holds_across_every_configuration`
covers 1, 2 and 4 replicas and requires under 2% relative error; the measured
worst case is 0.0813%.
`replicas_are_filled_evenly` and `adding_replicas_never_reduces_capacity` pin
the scaling behaviour that the original design got wrong.
