# ADR-001 — DB-backed outbox queue vs external broker

## Context

The system must deliver notifications reliably (never lose one), support
retry with backoff, per-tenant fairness, and a dead-letter queue, while
running on a host without Docker, without RabbitMQ, without Redis, and
without Postgres. The build must be green with only the .NET SDK installed.

## Options

1. **In-memory queue.** No persistence. Loses messages on restart.
2. **Redis / RabbitMQ.** Real broker semantics. Requires external infra.
3. **EF Core outbox in the primary database.** The queue is a table.
4. **Azure Storage Queue / Service Bus.** Requires cloud connectivity and
   secrets in a case-study context.

## Decision

Adopt **option 3**: an EF-backed outbox in SQLite. Notifications are the
outbox: rows carry status, attempts, next-attempt time, and are pulled by a
hosted worker with `FOR UPDATE`-equivalent behaviour via optimistic
concurrency.

## Consequences

- Zero infrastructure — the build reproduces on any Windows/Linux/Mac host
  with only the .NET SDK.
- Deterministic tests — the outbox is queryable and observable from tests.
- Bounded throughput — SQLite serialises writes; measured throughput is in
  `docs/throughput-test.md`.
- Portability — swapping SQLite for Postgres is a `UseNpgsql` change plus a
  couple of index migrations. The domain and pipeline do not change.

## Risks

- Long-running transactions could hold the writer lock. Mitigated by pulling
  batches and doing all provider I/O outside the DB transaction.
- Multi-instance deployment would need row-level locking or a leased worker
  registry. Called out as a Future Improvement.

## Alternatives revisited

If this project ever hosts real production traffic, an external broker
(RabbitMQ or Postgres LISTEN/NOTIFY on Postgres) becomes the right answer.
That change is scoped to the `NotificationPlatform.Infrastructure` project.
