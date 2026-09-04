# Upwork Portfolio Description

**Subscription Billing & Usage Metering Engine — self-directed engineering case study**

Problem: Recurring SaaS billing needs reproducible historical prices and safe handling of metered usage, mid-cycle changes, duplicate jobs, and failed collections.

Built: A .NET 10 reference implementation with versioned plans, seven pricing models, immutable usage rollups, to-the-second proration, idempotent invoices, discounts/credits/tax, payment simulation, dunning, reporting, and signed webhooks.

Engineering focus: minor-unit reconciliation, month-end anchors, database-backed idempotency, append-only financial evidence, replay defense, retry/dead-letter workflows, and SQLite integration testing.

Stack: ASP.NET Core minimal APIs, EF Core, SQLite, JWT policies, OpenTelemetry, xUnit.

Verification: Release build plus unit and API/infrastructure suites recorded in the repository. Docker configuration is authored but unverified.

This is a self-directed portfolio project, not client work.
