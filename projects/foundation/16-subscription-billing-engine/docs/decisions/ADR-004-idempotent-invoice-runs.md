# ADR-004 — Idempotent invoice runs enforced by the database

## Context

Background timers, manual operations, process restarts, and multiple workers can all run the same billing period more than once.

## Options

1. Trust a scheduler to invoke once.
2. Check for an invoice before insert.
3. Check for readability and enforce uniqueness in the database.

## Decision

Use both an existence check and a unique key on `(subscription_id, period_start, period_end, billing_reason)`. Periodic invoices use the stable `periodic` reason; immediate proration invoices use their change ID. The runner treats a unique collision as another worker winning. Invoice number allocation, financial rows, outbox records, usage closure, and period advancement share the EF unit of work.

## Consequences

Repeated runs cannot bill a period twice, and recovery does not depend on scheduler guarantees. The signature invariant is tested against SQLite.

## Risks

SQLite serializes more writes than a production multi-writer database. PostgreSQL deployment should add worker leasing while retaining the unique constraint.

## Alternatives

Distributed locks alone can expire or partition. An application-only pre-check has a race and is insufficient.
