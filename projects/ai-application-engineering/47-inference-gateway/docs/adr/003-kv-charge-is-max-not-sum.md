# ADR 003: A sequence charges `max(held, reserved)`, not their sum

**Status.** Accepted.

## Context

A running sequence occupies KV cache in two senses.

It has a **reservation** made at admission time. The gateway must decide whether
a request fits before it knows how long the output will be, so it reserves
`reservation_factor × predicted_peak_tokens` based on the estimator's guess.

It has **actual tokens held**, which start at the prompt length and grow by one
per generated token.

These overlap. The reservation is a claim on future growth; the held tokens are
the part of that growth which has happened. Something has to decide what the
sequence charges against capacity.

## Decision

```rust
pub fn charge(&self) -> u32 {
    self.kv_held.max(self.reserved_kv)
}
```

A sequence charges its reservation until it outgrows it, and its actual usage
thereafter.

## Consequences

**Summing double-counts.** The first implementation charged
`kv_held + reserved_kv`. A sequence that had reserved 1000 tokens and grown into
300 of them was billed 1300. Mean batch size collapsed from a configured maximum
of 48 to about 14, and SLO attainment never exceeded 60% at any utilisation —
including at ρ=0.4, where the system should be almost idle.

The failure signature is worth recording: *no parameter setting made the physics
plausible*. Tuning that cannot reach a sensible operating point is a modelling
error, not a tuning error.

**Charging only `kv_held` makes reservation a no-op.** If a sequence charges
only what it currently holds, admission decisions are made against memory that
is guaranteed to be consumed later, and the whole point of admitting on a
projection disappears. Section 10 — which finds that the optimal reservation
factor is 0.85 when memory binds and below 1.0 always — would have nothing to
measure.

**A second predicate is needed for admission.** `charge()` answers "how much is
this sequence using now". Admission also needs "how much more will the existing
batch consume on its next step", because a gateway that admits into memory its
own decode step is about to claim will evict immediately. That is
`Replica::needs_fresh_kv`, which sums `grows_next_token()` over the batch, and
its absence was the proximate cause of the eviction livelock.

**Two estimates, not one.** Because the reservation is computed from a
prediction, and the scheduler's ordering is *also* computed from a prediction,
`GatewayConfig` carries `estimator_spread` and `reservation_spread` separately.
Collapsing them into one knob confounded section 6 — see
[ADR 004](004-separate-the-two-estimate-channels.md).

**Pinned by.** `engine_test.rs::charge_is_max_not_sum`,
`charge_follows_actual_use_once_the_reservation_is_exceeded`,
`charge_never_falls_below_the_reservation`,
`a_sequence_inside_its_reservation_does_not_need_fresh_memory`,
`a_sequence_past_its_reservation_needs_fresh_memory`, and
`admission_reserves_headroom_for_the_existing_batch`.
