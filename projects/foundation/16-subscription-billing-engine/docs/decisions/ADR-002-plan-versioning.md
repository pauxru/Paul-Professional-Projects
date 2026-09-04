# ADR-002 — Immutable plan versions for reproducibility

## Context

Changing a plan's current price must not alter subscriptions already contracted to an earlier price or historical invoice reconstruction.

## Options

1. Update a mutable price row.
2. Copy price values onto every subscription.
3. Keep a stable plan identity with append-only effective-dated versions.

## Decision

Plans are commercial identities and intervals; each pricing change appends a numbered `PlanVersion`. A subscription stores the exact version ID. Version rows are append-only, and `(plan_id, version)` plus `(plan_id, effective_from)` are unique.

## Consequences

Invoices and previews can resolve the exact strategy/configuration originally selected. Catalogue readers can still ask which version was effective at an instant.

## Risks

Version accumulation requires retention/index discipline. Incorrect versions cannot be edited; a corrective version must be appended and affected subscriptions changed explicitly.

## Alternatives

Snapshotting price fields on subscriptions reduces joins but duplicates configuration and weakens catalogue lineage. Event sourcing could offer richer history but is unnecessary for this scope.
