# ADR 004: Separate the scheduling estimate from the reservation estimate

**Status.** Accepted. Changed the design of section 6 after its first version
produced a confounded result.

## Context

An inference gateway cannot know how long a request will run: output length is
decided by the model, one token at a time. Every size-aware mechanism therefore
runs on a prediction.

There are two such mechanisms, and originally both read the same `Estimate`:

1. The **scheduler** uses predicted cost to order the queue (SJF, aged SJF, and
   DRR's token charging).
2. The **memory manager** uses predicted peak length to size the KV reservation
   at admission.

Section 6 asks how accurate that prediction needs to be. The obvious experiment
is to vary the estimator's error spread and watch the system degrade.

## Decision

`GatewayConfig` carries two independent knobs, `estimator_spread` and
`reservation_spread`, and `Queued` carries two estimates, `est` and `kv_est`.
Section 6 varies each alone and then both together.

## Consequences

**The single-knob experiment was confounded, and would have shipped as a
finding.** Varying "estimator accuracy" moved both mechanisms at once, and the
measured effect was their sum: a muddy result in which SJF's advantage decayed a
little and the honest summary would have been "prediction accuracy matters
somewhat".

**The giveaway was a control that should not have moved.** FIFO's numbers
changed with estimator spread. FIFO never consults the estimate for ordering. If
FIFO is sensitive to estimator error, the error is reaching the system through
some channel other than scheduling.

**Separating the channels produced the report's most useful finding:**

| channel varied | effect of 8× estimate error |
|---|---|
| scheduling only | SJF advantage +0.094 → +0.085 (flat) |
| reservation only | attainment 91.4% → 75.6%, evictions 0 → 137 |
| both | attainment 95.1% → 88.3% |

Scheduling needs the **ranking** to be roughly right, and rank statistics are
robust to multiplicative noise — a chat turn and a batch job differ by more than
an order of magnitude, so even 8× noise rarely swaps them. A memory reservation
needs the **magnitude**, and being wrong about it commits resources that
preemption can redistribute but never recover.

The rule this yields is sharper than "improve the predictor": use predictions
for ordering decisions, which are reversible and forgiving, and avoid using them
for resource commitments, which are neither. If a commitment must be made from a
prediction, bias it in the direction whose failure mode is cheaper — which is
what section 10 measures.

**Generalisation.** Vary one channel at a time, and keep a control that should
not respond. The confound was only visible because FIFO was in the table; had
the experiment compared SJF against SJF, the result would have looked clean and
been wrong.

**Pinned by.** `sched_test.rs::fifo_ignores_the_estimate_entirely`, which feeds
two identical FIFO schedulers wildly different estimates and requires identical
service order. If that test fails, section 6 is confounded again.
`sjf_ranks_on_the_estimate_not_the_truth` pins the complementary property: the
scheduler must never see the true output length, or every scheduling result in
the report is inflated by an oracle.
