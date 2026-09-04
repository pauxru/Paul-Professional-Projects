# Upwork Portfolio Description

## Short version

**Multi-Tenant B2B Field Operations SaaS — self-directed engineering case study**

Problem: A shared SaaS must prevent customer data crossover while enforcing live roles, plan limits, feature rollout and billing status.

Built: A .NET 10 platform for assets, jobs and typed inspections with a functional administration dashboard.

Engineering focus: tenant resolution, EF global filters plus write interception, policy RBAC/cache invalidation, entitlements and atomic metering, deterministic flags, signed replay-safe billing webhooks, dunning and append-only audit.

Stack: ASP.NET Core, EF Core, SQLite, JWT, OpenTelemetry, xUnit and vanilla JavaScript.

Verification: Release build and 90 automated unit/integration cases execute with no external infrastructure. Exact current output is stored in `docs/test-results.md`.

This is a self-directed portfolio project, not client work.

## Suitable engagements

- multi-tenant ASP.NET Core architecture and isolation reviews;
- SaaS plan, quota, billing and entitlement implementation;
- API authorization/IDOR remediation;
- modernization toward modular monoliths and ports/adapters;
- reliability testing for webhooks and idempotent integrations.
