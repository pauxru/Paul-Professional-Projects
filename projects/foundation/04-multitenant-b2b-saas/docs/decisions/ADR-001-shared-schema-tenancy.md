# ADR-001 — Shared schema with tenant discriminator

**Status:** Accepted
**Date:** 2026-09-03

## Context

FieldOps must support several organizations while running locally without database servers. The design must make isolation demonstrable, not merely state that every query should include a tenant predicate.

## Options

1. One database per tenant.
2. One schema per tenant.
3. Shared schema with `TenantId` discriminator.
4. Hybrid tiers: shared by default, dedicated stores for selected tenants.

## Decision

Use a shared SQLite schema with `TenantId` on every tenant-owned row. `Organization` and global `AppUser` are platform records. Composite indexes begin with `TenantId`, and isolation is enforced by scoped context, EF filters, application guards and a write interceptor.

## Consequences

- Local setup, testing and platform-wide reporting are simple.
- One migration applies to all tenants.
- Tenant context is mandatory in data paths.
- A persistence defect could have broad blast radius, so multiple independent controls and tests are required.

## Risks

- Incorrect unfiltered raw SQL could disclose data.
- Platform-admin paths could become IDOR surfaces.
- Large tenants may cause noisy-neighbor behavior.

## Alternatives

Database-per-tenant gives the strongest physical boundary and independent restore, but increases connection, migration and reporting complexity. Schema-per-tenant is poorly aligned with SQLite and still multiplies migrations. A hybrid is a credible future enterprise tier once tenant placement/routing exists.
