# ADR-005 — Immutable usage events with correction events

## Context

Editing metered usage destroys the audit trail and makes invoice reproduction ambiguous. High-volume billing cannot scan every event at each invoice run.

## Options

1. Update or delete erroneous events.
2. Store only mutable aggregates.
3. Append immutable events, represent corrections as linked adjustments, and maintain rollups.

## Decision

Usage events are append-only and unique by caller-supplied event ID. A correction is another event with `adjustmentOfEventId`; negative quantities are permitted only for such adjustments. Each open period maintains an indexed rollup supporting sum, max, last-value, or unique-count aggregation and explicit meter rounding.

## Consequences

Retries are safe, raw evidence remains available, and normal invoicing reads one rollup row rather than an event scan. Closed-period handling is configurable as reject or credit into the next open period.

## Risks

Corrections for max/last/unique semantics need business-specific interpretation at larger scale. This implementation treats adjustment events as immutable participants and optimizes the normal ingestion path.

## Alternatives

Mutable events are operationally simple but financially unsafe. Aggregate-only storage loses source evidence and correction lineage.
