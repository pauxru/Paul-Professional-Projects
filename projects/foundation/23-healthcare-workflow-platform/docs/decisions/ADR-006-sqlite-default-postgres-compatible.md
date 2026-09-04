# ADR-006 — SQLite by default, Postgres-compatible

**Status:** Accepted
**Date:** 2026-09-01

## Context
The build host and the portfolio review environment do not have Docker, Postgres, or Redis
available. A production deployment would use Postgres. The persistence layer must:
- run out-of-the-box on `dotnet run`, no external services;
- run identical tests deterministically;
- migrate cleanly to Postgres when needed.

## Options
1. **SQLite only:** simplest; not suitable for scale.
2. **Postgres only:** requires Docker or a hosted DB; blocks local run.
3. **Provider abstraction (this ADR):** a single EF Core model with SQLite as the default
   and Postgres selected by connection-string prefix.

## Decision
The `AppDbContext` is registered with SQLite by default. If the `ConnectionStrings:Default`
value starts with `Host=` or `Server=`, `Program.cs` selects Postgres via
`UseNpgsql(...)` instead of `UseSqlite(...)`.

Every schema feature we depend on is compatible with both:
- Filtered unique indexes: SQLite supports `WHERE` clause on `CREATE UNIQUE INDEX`;
  Postgres supports the same syntax.
- `DateTimeOffset`: stored as ticks (`long`) via a value converter for SQLite ordering;
  the same converter works on Postgres (though not strictly needed there).
- Enum columns are stored as `int` (not `string`) so filter clauses like
  `"Status" NOT IN (7, 8)` are portable.

Integration tests use an **open in-memory SQLite** (`DataSource=:memory:`) shared per test
class fixture. That gives real SQL and real transactions, not an in-memory provider fake.

## Consequences
- The developer experience is one command (`dotnet run`), zero services to install.
- Production migration to Postgres is a connection-string change.
- Every schema decision has to work in both providers; we lose some Postgres-specific
  power (window functions, JSONB, RLS). None of these are needed for the current features.

## Risks
- SQLite has weaker concurrency semantics than Postgres. Under real production load a
  move to Postgres is required. The unique-index-based booking constraint works in both.

## Alternatives considered
- LocalDB (SQL Server): rejected — not available on Linux runners; not required by the
  spec.
- Cosmos DB or DynamoDB: rejected — over-scoped and requires cloud services.
