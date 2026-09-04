# ADR-005: Append-only enforcement — defence in depth

*Status*: Accepted
*Date*: 2026-01-15

## Context

The value proposition of the platform depends on *no code path* ever mutating or deleting an
audit event. A single successful attack anywhere in the stack destroys the property.

## Decision

Enforce append-only at **three independent layers**.

### Layer 1 — Compile-time barrier

- Every property on `AuditEvent` (and `Checkpoint`, `RetentionPolicy`, `LegalHold`,
  `SavedQuery`, `EventSchema`, `DeadLetterEvent`) has a `private set`.
- The only exposed mutation is `AuditEvent.Tombstone(when)`, which is intentional and audited.
- Instantiation flows through a `public static CreateInternal` factory used exclusively by the
  ingest service.

### Layer 2 — Runtime barrier (`AppendOnlyInterceptor`)

- An EF Core `SaveChangesInterceptor` scans the change tracker on every save.
- Any `AuditEvent` in `Deleted` state throws `DomainException(AppendOnlyViolation)`.
- Any `AuditEvent` in `Modified` state throws unless the mutation is a legitimate tombstone
  transition (see ADR-004).
- Any `Checkpoint` in `Deleted` state throws.
- Any `Checkpoint` in `Modified` state throws unless it is the one-shot signature attach.

This layer catches attacks at the ORM boundary — bad tests, buggy new features, an attacker
who obtains DbContext-level access via a service vulnerability.

### Layer 3 — Database-level revocation (production hardening; not implemented on SQLite)

- The application role has `SELECT, INSERT` on `AuditEvents`, `Checkpoints`, and friends —
  **no `UPDATE`, no `DELETE`**.
- The tombstone-and-signature-attach paths run under a distinct role with narrower permissions.
- Archived evidence packs live on WORM storage.

Not implemented in this repository because SQLite does not enforce ROLE grants; documented as
the production-hardening layer.

## Rationale

- Any single layer is insufficient. A DB permission change bypasses layer 1 (private setters
  don't help against a rogue query builder). A missing interceptor bypasses layer 2. A bug in
  the interceptor is caught by layer 3.
- Each layer is easy to reason about in isolation.

## Consequences

- The interceptor test suite is a first-class deliverable: `Interceptor_BlocksUpdate…`,
  `Interceptor_BlocksDelete…`.
- Any future field addition on `AuditEvent` must be a new private-set property with a
  compile-time constructor argument, not a mutable one.

## Alternatives considered

- **Triggers** — DB-side triggers are an option but Postgres-specific; deferred to production
  hardening.
