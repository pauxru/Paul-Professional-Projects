# ADR 0002 — An exact linearizability checker, not a sampled one

## Status
Accepted.

## Context

Checking whether a concurrent history is linearizable is NP-complete in general.
For a single register the search is still exponential in the number of
concurrently overlapping operations.

The tempting alternatives:

1. **Check a weaker property.** Sequential consistency, or per-client
   monotonicity. Cheap, but the bug this repository is about *is* a
   linearizability violation that satisfies weaker properties — every client
   individually sees a sensible sequence.
2. **Sample orderings.** Try some random linearizations; if one works, pass.
   Fast, and unsound in the direction that matters: it reports "linearizable"
   for histories that are not.
3. **Bound the search and give up.** Returns "unknown", which in practice gets
   treated as "fine".

## Decision

Run the exact Wing–Gong search, and size the workload so it stays affordable.

The search maintains a bitmask of linearized operations plus the current
register value, and memoises `(bitmask, value) -> false`. An operation may be
linearized next only if no unlinearized operation completed before it was
invoked. Operations that never returned are allowed to be linearized anywhere,
or not at all — a crashed write genuinely may or may not have taken effect.

Measured cost at the workloads used here:

| operations in history | ms per run |
|---|---|
| 7 | 0.03 |
| 15 | 0.06 |
| 22 | 0.07 |
| 29 | 0.10 |

The bitmask caps the history at 60 operations, enforced by an assertion with a
message telling you to shrink the workload rather than weaken the checker.

## Consequences

A "linearizable" verdict is a proof, not an opinion. That is what lets the
harness assert *zero* violations for the correct protocol across 2,000 seeds and
have the assertion mean something.

The cost is a hard ceiling on workload size. Long-running soak tests with
thousands of operations are impossible. This turns out to matter less than
expected: the sweep in `docs/results.md` shows detection rate is driven by
*concurrency shape*, not history length, and short histories with many clients
find the bug more readily than long ones.

The checker also reports *which* operation is unexplainable, by re-running the
search with each completed read excluded in turn. Candidates are tried
latest-completing first: in this failure mode two reads disagree and both
removals repair the history, but the later read is the one that went backwards
and is the one an engineer needs to see.
