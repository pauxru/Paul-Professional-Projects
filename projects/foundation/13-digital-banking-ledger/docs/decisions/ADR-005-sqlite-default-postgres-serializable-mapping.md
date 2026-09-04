# ADR-005 — SQLite as the default store and the Postgres `SERIALIZABLE` mapping

- **Status**: Accepted
- **Date**: 2026
- **Context tags**: persistence, reproducibility, concurrency

## Context

The project must **build and pass its tests on a machine that has only the .NET SDK** — no Docker, no
database server, no network dependencies. At the same time, a banking ledger's natural production
home is a server-grade RDBMS with strong isolation (Postgres). The design must satisfy the
zero-infrastructure constraint *without* baking in decisions that would be wrong on Postgres.

## Options considered

1. **Postgres-only** (requires a running server / Docker).
2. **In-memory store** (no real persistence semantics, weak fidelity).
3. **SQLite default via EF Core, Postgres-compatible model**, with the concurrency protocol written
   so it maps cleanly onto Postgres.

## Decision

Use **EF Core with SQLite as the committed default** (`Data Source=ledger.db`), created with
`EnsureCreated` and seeded on startup. Keep the EF model provider-agnostic (all monetary columns are
`long`; enums are stored as strings; a connection interceptor sets `busy_timeout=5000` and
`foreign_keys=ON`). Design the concurrency protocol (ordered account locks → chain lock → commit,
with optimistic version tokens, retry, and idempotency) so it is a **shape-for-shape** analogue of a
Postgres implementation.

## Consequences

**Positive**
- **Zero-infrastructure reproducibility**: `dotnet build` / `dotnet test` work with only the SDK; the
  integration tests run the full stack against a real (file-backed) SQLite database.
- The model and application code are portable to Postgres without redesign.
- Tests exercise real SQL, transactions, and unique-constraint behaviour (not an in-memory fake).

**Negative / costs**
- SQLite is **single-writer** and single-node, so the in-process `AccountLockManager` is required to
  serialize appends and is only correct within one process (see ADR-003).
- Some Postgres features (true `SERIALIZABLE`, `SELECT … FOR UPDATE`, advisory locks) are simulated at
  the application layer rather than delegated to the database.

## The Postgres mapping (honest)

| Concern | SQLite default (this repo) | Postgres |
|--------|-----------------------------|----------|
| Writer serialization | single-writer DB + global in-process chain lock | `SERIALIZABLE` isolation, or an advisory lock on the sequence |
| Per-account locking | `SemaphoreSlim` per account, ascending-id order | `SELECT … FOR UPDATE` on account rows in ascending-id order (same order ⇒ deadlock-free) |
| Conflict handling | retry `ConcurrencyConflictException`/transient | retry `40001 serialization_failure` / `40P01 deadlock_detected` |
| Optimistic tokens | `Version` concurrency columns | keep `Version`, or rely on `SERIALIZABLE` |
| Idempotency | unique index on key + stored response | identical |
| Connection pragmas | `busy_timeout`, `foreign_keys=ON` | connection defaults / `statement_timeout` |

**How SQLite differs**: SQLite allows only one write transaction to commit at a time, so within a
single process the global chain lock is sufficient and MVCC write-write conflicts don't arise the way
they do on Postgres. The trade is that SQLite cannot coordinate writers **across processes/nodes** —
that is exactly what moving to Postgres (row locks + `SERIALIZABLE` + a serialization-failure retry
loop) buys, and the executor's retry-on-conflict path is already written in that shape.

## Risks and mitigations

- **Risk**: someone assumes the SQLite single-node model scales horizontally. **Mitigation**: called
  out as a Known Limitation in the README and here; the mapping above is the migration path.
- **Risk**: provider-specific SQL creeps in. **Mitigation**: the model uses portable EF conventions
  only; no raw SQLite SQL in the domain/application.

## Alternatives not chosen

- *Postgres-only* — rejected: violates the zero-infrastructure build constraint.
- *In-memory* — rejected: poor fidelity; would not exercise real constraints/transactions in tests.
