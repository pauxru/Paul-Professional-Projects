# ADR-002 — Query filters plus write interceptor

**Status:** Accepted
**Date:** 2026-09-03

## Context

Developers can forget a repository predicate, call `IgnoreQueryFilters`, attach a detached entity or mutate its discriminator. Read and write isolation need independent enforcement.

## Options

1. Repository predicates only.
2. EF global query filters only.
3. Database row-level security only.
4. Global filters plus application guards plus SaveChanges interception.

## Decision

Apply EF global query filters to every `ITenantOwned` entity. Add a scoped `TenantSaveChangesInterceptor` that stamps empty insert IDs, compares current and original tenant IDs for writes, logs a security event and rejects mismatches. Cross-aggregate application services also call `TenantGuard`.

## Consequences

- Ordinary reads are safe by default.
- A deliberately forgotten filter can read only when explicitly bypassed, but cannot persist a foreign mutation.
- Tests can inspect model metadata and exercise a malicious path.
- Seeding and administration must deliberately establish or bypass tenant context.

## Risks

- Raw SQL outside EF could bypass both filters and the interceptor.
- Background work must propagate tenant context correctly.
- Interceptor failure is fail-closed and may surface operational mistakes as rejected writes.

## Alternatives

Repository predicates were rejected as too easy to omit. Row-level security is desirable in a production PostgreSQL/SQL Server adapter but unavailable in default SQLite and should complement, not replace, application controls.
