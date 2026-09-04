# ADR-003 — Proration by remaining service time to the second

## Context

Day-based proration produces inconsistent results for same-day changes and periods with different lengths. Separate rounding of old credit and new charge can also miss the intended net by one minor unit.

## Options

1. Prorate by whole calendar day.
2. Prorate by actual remaining seconds and round each line independently.
3. Prorate by actual remaining ticks, round the intended net once, then reconcile lines.

## Decision

Use the ratio `(period_end - change_at) / (period_end - period_start)` at tick precision, equivalent to elapsed/remaining time to the second for API timestamps. Calculate the previous-price credit, calculate the intended net once, and derive the new charge so `credit + charge == intended net` exactly. Support `create_prorations`, `none`, and `always_invoice`.

## Consequences

Same-day and multiple changes remain deterministic; every result reconciles in minor units. The charge line may differ by one minor unit from independently rounded math because reconciliation is prioritized.

## Risks

Clock/time-zone policy must be stable. All persisted instants are normalized to UTC ticks; local calendar presentation is separate.

## Alternatives

Calendar-day proration is easier to explain but fails precision expectations. Provider-specific algorithms could be adapters, but the core needs one documented rule.
