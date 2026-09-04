# ADR 0005 — Straggler links belong in the fault model

## Status
Accepted. This one was driven by a measurement that contradicted the design.

## Context

The first fault model had what everybody's fault model has: message loss,
duplication, reordering, network partitions, and clock skew. It looked
comprehensive.

Against the broken protocol it found violations in **0.2% to 1.2%** of runs
depending on workload shape. That is a working harness, but it is a weak one:
finding a bug once in 500 runs means a CI job of 100 seeds usually reports
green.

## Context: what the bug actually needs

The ABD read without write-back is unsafe in one specific situation:

1. A write has stored to *some* replicas but has not yet reached a majority, so
   it has not returned.
2. A read whose quorum happens to include one of those replicas returns the new
   value.
3. A later read whose quorum happens to miss all of them returns the old value.

Step 1 requires a *persistently* uneven write. Loss removes messages but the
sender's other messages still land. Reordering delays one message, but only
sometimes, and only once. Partitions cut a set of links but heal on a tick, and
while cut, nothing gets through at all — which usually just stalls the write.

None of those reliably produce "this replica is consistently behind for a
while", which is exactly the condition real clusters produce constantly, via a
slow disk, a saturated NIC, a noisy neighbour, or a GC pause.

## Decision

Add per-directed-link stragglers. At simulation setup each of the `n²` directed
links is marked slow with probability `slow_link_prob` (default 0.25), and slow
links multiply latency by `slow_factor` (default 12). The marking is a property
of the run, drawn from the seed, and does not change during it.

## Consequences

Violation rate at the best workload shape went from 0.6% to **2.6%**, roughly a
4× improvement. The ablation table confirms it is load-bearing: disabling
stragglers alone drops the overall rate from 2.30% to 1.25%, second only to
reordering.

| fault disabled | rate |
|---|---|
| none | 2.30% |
| message loss | 2.00% |
| duplication | 2.45% |
| reordering | 1.40% |
| partitions | 2.25% |
| clock skew | 2.55% |
| straggler links | 1.25% |

The wider lesson is the one worth keeping. **A fault model is not judged by how
many fault types it has; it is judged by whether it produces the conditions bugs
need.** Four of the six faults here are nearly irrelevant to this particular
bug — disabling clock skew or duplication actually *raised* the observed rate,
which is noise at this sample size but is certainly not evidence they help.

That result generalises uncomfortably. Anyone assembling a chaos-testing setup
by collecting fault types is optimising the wrong quantity. The right move is to
ask what state the bug class needs, then inject the fault that produces it, then
measure whether it did.
