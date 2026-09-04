# ADR-004 — Use Expand/Contract Database Migrations

## Context

Stable and candidate revisions share one database during progressive release. A destructive schema change would make application rollback unsafe.

## Options

1. Stop-the-world migration and deploy.
2. In-place rename/drop in one release.
3. Expand, backfill, dual-write, switch reads, then contract.

## Decision

Use expand/contract. The worked example adds nullable `description_v2`, backfills it, records dual-write and read-switch phases, then drops `description` only after the compatibility window.

## Consequences

- Old and new revisions can run concurrently.
- Migration jobs run before traffic changes.
- Contract steps require a separate change and explicit approval.
- Temporary schema and code complexity is accepted.

## Risks

- Backfills can create load and replication lag.
- Dual-write paths can diverge without monitoring.
- Dropping the old column is irreversible without restore or a compensating migration.

## Alternatives

A maintenance window may be acceptable for low-criticality systems, but it reduces availability and rollback options. Shadow tables are suitable for larger transformations but add replication and cutover complexity.
