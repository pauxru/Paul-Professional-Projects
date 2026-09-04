# ADR-003 — SQLite by default with Postgres as a configuration-selected adapter

- Status: Accepted
- Date: 2025-11-24
- Deciders: @pauxru (solo)

## Context

Reviewers must be able to build and test this project on a clean host without
Docker, without a Postgres server, and without any external services. That is
a hard constraint of the portfolio spec. At the same time, the "flagship
payment platform" positioning is unconvincing if the design cannot be moved
to a production-grade database.

## Options

1. **In-memory DB (`Microsoft.EntityFrameworkCore.InMemory`).** Fastest to
   run, but it does not model relational semantics — transactions,
   optimistic concurrency, UNIQUE indexes all behave differently. Bad choice
   for a payment platform: the very invariants we want to prove would not be
   real.
2. **SQLite everywhere.** Real relational engine, real transactions, real
   UNIQUE indexes, real concurrency behaviour (with limits). Runs in
   `:memory:` for tests and file-based for local demo. **Chosen for default.**
3. **Postgres everywhere.** Realistic production choice, but requires a
   running server. Fails the "zero external infra" contract.
4. **Two providers behind a switch.** Compose SQLite + Postgres both via
   `DbContextOptions` and choose via configuration. **Chosen for the
   Postgres adapter.**

## Decision

- The default provider is SQLite via `Microsoft.EntityFrameworkCore.Sqlite`.
- `DatabaseOptions.Provider` is a string. When set to `Sqlite` (default), we
  register the SQLite provider. When set to `Postgres`, the composition root
  throws a clear `NotSupportedException` explaining the configuration switch
  is present but unverified on this host, and points at Npgsql.
- All EF configuration is provider-agnostic (owned types, HasIndex, HasKey).
  No SQLite-specific SQL is authored anywhere in code.
- Two provider-shaped behaviours are worked around explicitly:
  - `DateTimeOffset` columns use `DateTimeOffsetToBinaryConverter` globally so
    `WHERE NextAttemptAtUtc <= now` translates on SQLite.
  - Guid primary keys use `ValueGeneratedNever` globally so EF trusts our
    domain-supplied Guid on `INSERT` instead of issuing an `UPDATE`.
- All tests use SQLite (shared-cache `:memory:` for integration).

## Consequences

- **Positive** — `dotnet test` runs anywhere without any setup. This is
  the difference between "runs on the reviewer's laptop" and "doesn't."
- **Positive** — the domain / application layers stay database-agnostic.
- **Negative** — SQLite is single-writer; the parallel-reservation test
  observes only one successful reservation per contention burst. In a real
  deployment Postgres would allow the domain's optimistic-concurrency check
  to be the sole serialization point.
- **Negative** — Postgres adapter is committed but untested on this host.
  The runbook explains what to change to run it.

## Risks

- Anyone flipping the switch to Postgres must configure Npgsql and add a
  connection string. Startup fails fast rather than silently continuing on
  SQLite, so the mistake surfaces immediately.
- SQLite is not a production database. This is explicit in the README's
  "Known Limitations" and in the runbooks.

## Alternatives revisited

If we make Postgres the default in a future revision, the switch is
localised to `CompositionRoot.AddPaymentsPlatform` and one line of the
`.env.example`. Everything else in the codebase is unchanged.
