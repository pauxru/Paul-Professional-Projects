# ADR-004 — Treat current vehicle state as a replayable projection

## Status

Accepted.

## Context

Current vehicle position, speed and trip context are derived from telemetry. A projection bug, changed rule or accidental row loss must be recoverable without asking devices to resend data. Updating only a mutable current-state row would discard the evidence needed to reconstruct an incident.

## Options

1. Store only current state.
2. Store source pings and update current state synchronously, but provide no rebuild.
3. Retain source pings and make `VehicleStates` disposable/rebuildable through the same partitioned processor.
4. Adopt full event sourcing for every fleet aggregate.

## Decision

Use option 3. `VehiclePings` is the retained telemetry source, protected by an idempotency key. `VehicleStates` is a persisted read model with an in-memory processing counterpart. Projection rebuild deletes only state rows, clears volatile rule calculators and republishes retained pings in per-vehicle sequence order.

Replay envelopes are marked. They update state, can re-evaluate idempotent alerts, but do not append source events, duplicate ETA history or mutate historical trip transitions.

## Consequences

- Operational state can be reconstructed deterministically.
- Incident replay uses the production processing path instead of a separate script.
- The source event table grows and needs lifecycle/partition management in production.
- Projection schema can evolve independently if a versioned rebuild is available.

## Risks

- Rebuilding a very large history in one database can contend with live traffic.
- Any non-deterministic rule input must be captured or versioned.
- A replay against current configuration may differ from the historical configuration.
- Clearing volatile suppression state relies on the database fingerprint as the final alert idempotency boundary.

## Alternatives

Full event sourcing was rejected as unnecessary for fleet registry/planning aggregates. Production could build a shadow projection, compare counts/checksums, then atomically swap versions instead of deleting the live projection first.
