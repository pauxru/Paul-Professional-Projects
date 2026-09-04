# 03 — The fault that mattered, and the sweep that contradicted me

Two measurements in this project changed what I built. Both contradicted a prior
I would have defended.

## One: the fault model was comprehensive and weak

The first version modelled message loss, duplication, reordering, asymmetric
network partitions, and per-node clock skew. That is the standard list.

Against the broken protocol it found violations in **0.2% to 1.2%** of runs. A
CI job of 100 seeds would report green most of the time. The harness worked and
was nearly useless.

The mistake was counting fault *types* instead of asking what state the bug
needs. The unsafe read requires a write that has reached some replicas and not
others and **stays that way** long enough for two reads to straddle it. None of
the five faults produce that:

- **Loss** removes a message, but the sender's other messages still arrive
  promptly, so the write either lands widely or stalls entirely.
- **Reordering** delays a message once, and only sometimes.
- **Partitions** cut links, but while cut *nothing* gets through, which usually
  just stalls the write rather than skewing it. And they heal on a tick.
- **Duplication** and **clock skew** do not affect propagation shape at all.

What produces a persistently uneven write is a **straggler**: a link that is
consistently slow for the whole run. Which is what real clusters produce
constantly — a slow disk, a saturated NIC, a noisy neighbour, a GC pause.

Adding per-directed-link stragglers (25% of links, 12× latency) took the best
observed rate from 0.6% to **2.6%**. The ablation confirms it:

| fault disabled | rate |
|---|---|
| none | 2.30% |
| message loss | 2.00% |
| duplication | 2.45% |
| **reordering** | **1.40%** |
| partitions | 2.25% |
| clock skew | 2.55% |
| **straggler links** | **1.25%** |

Reordering and stragglers do essentially all the work. Disabling clock skew or
duplication *raised* the measured rate, which at this sample size is noise — but
it is certainly not evidence they help.

I would not have predicted that ranking, and I would have been fairly confident
about it. Which is the argument for the ablation table existing at all: a fault
model is a hypothesis about what causes bugs, and hypotheses should be measured.

## Two: bigger clusters hide the bug

My prior: more replicas means bigger quorums, more ways for two reads to pick
different subsets, more violations. It seemed obvious enough not to check.

The sweep (500 seeds per cell, broken protocol):

| replicas | clients | ops/client | rate |
|---|---|---|---|
| 3 | 4 | 6 | **2.6%** |
| 5 | 3 | 6 | 1.2% |
| 5 | 4 | 4 | 1.0% |
| 7 | 4 | 4 | 0.2% |
| 7 | 3 | 6 | **0.0%** |
| 7 | 2 | 6 | 0.0% |

Exactly backwards. At 7 replicas the bug is essentially undetectable.

The reason is clear in retrospect. The violation needs Read 1's quorum to
include a replica the write reached and Read 2's quorum to include none of them.
At 3 replicas the read quorum is 2 of 3 and there is one replica of slack. At 7
replicas the read quorum is 4 of 7 — a read has to miss *all* of the replicas
the write reached, while still hearing from four nodes. As the write propagates
even slightly, that becomes very unlikely very quickly.

More clients, on the other hand, help a lot: 3 replicas with 2 clients gives
0.4%, with 4 clients 2.6%. The bug needs two reads from *different* clients,
because a single client's two reads tend to take the same fast path and see the
same quorum.

## Why this is the important part

The instinct when a distributed test suite is not finding bugs is to make it
*bigger* — more nodes, longer runs, more operations. On this bug, every one of
those moves makes detection worse or leaves it flat.

The thing that worked was: characterise the condition the bug needs, inject the
fault that produces that condition, and sweep the workload shape to find where
it lives. That produced a 13× improvement (0.2% → 2.6%) over the configuration
I would have picked by intuition, and it came from measurement, not cleverness.

The uncomfortable corollary is in `docs/known-limitations.md`: **this harness
would not have found the bug at 7 replicas**, and 7 replicas is a perfectly
ordinary production configuration. A clean run is evidence about the workload
shape you searched, not about the protocol. Anyone reporting "we ran 10,000
simulations and found nothing" without saying which shapes they swept has
reported almost nothing.
