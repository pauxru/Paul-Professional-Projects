# ADR-002 — Database-as-queue trade-offs and the `SKIP LOCKED` mapping

- **Status:** Accepted
- **Date:** 2026-09
- **Context tags:** queueing, throughput, portability

## Context

Jobs need a durable work queue. The prime directive forbids Redis/RabbitMQ/Kafka and mandates a
SQLite-default, zero-infra build. The question is whether to use the relational database itself as
the queue, and how honest that is about production scale.

## Options considered

1. **Dedicated broker (RabbitMQ/Kafka/Redis Streams).** High throughput, native consumer groups.
   But external infra, at-least-once still needs app-level dedupe, and it splits the source of truth
   between broker and DB (a run's state lives in two places).
2. **In-memory queue.** Trivial, but not durable and not multi-process — defeats the whole point of
   demonstrating distributed coordination.
3. **Database-as-queue (chosen).** `JobRuns` *is* the queue. "Due work" is a query
   (`State='Pending' AND ScheduledAt<=now`); claiming is a conditional `UPDATE`. One source of
   truth for both queue position and run state.

## Decision

Use the **database as the queue**, with the claim implemented as a single guarded `UPDATE`
(compare-and-set). On SQLite this is safe because the store serialises writers; a `busy_timeout` +
WAL journal makes concurrent claims queue rather than error. The index `(State, ScheduledAt)` makes
the due-scan a range seek.

### The `SKIP LOCKED` mapping (production)

SQLite's single writer is the honest limitation. On PostgreSQL the identical protocol gains writer
parallelism:

```sql
-- PostgreSQL claim: many workers claim different rows concurrently, no head-of-line blocking.
WITH due AS (
  SELECT id FROM job_runs
  WHERE state = 'Pending' AND scheduled_at <= now()
  ORDER BY priority DESC, scheduled_at
  FOR UPDATE SKIP LOCKED          -- <- the key: skip rows another worker already locked
  LIMIT 1
)
UPDATE job_runs r
SET state='Claimed', lease_owner=$1, lease_token=$2,
    lease_expires_at = now() + interval '30 seconds',
    fencing_token = r.fencing_token + 1
FROM due WHERE r.id = due.id
RETURNING r.id, r.fencing_token;
```

`SKIP LOCKED` is exactly the semantic our SQLite `UPDATE … WHERE State='Pending'` approximates: a
worker never blocks on a row another worker is already claiming; it moves to the next candidate. The
application layer (`IJobRunStore`) is unchanged — only the infrastructure implementation swaps.

## Consequences

- **Positive:** One durable source of truth; run state, queue position, lease and history are the
  same row/table set. Trivial local development. Rich queryability (priority, aging, fan-in) for
  free via SQL + indexes. Portable protocol.
- **Positive:** Operators can inspect and repair the queue with plain SQL (see runbooks).
- **Negative:** Throughput ceiling. SQLite = one writer; even Postgres-as-queue tops out far below a
  dedicated broker (thousands/sec, not millions). Polling adds latency (`PollSeconds`) vs push.
- **Negative:** The due-scan must stay index-covered or it degrades under backlog — hence the
  deliberate `(State, ScheduledAt)` and `(State, LeaseExpiresAt)` indexes.

## Risks & mitigations

- **Hot-row contention / write amplification.** Mitigation: claim touches exactly one row; indexes
  bound every scan; WAL + `busy_timeout` avoid `SQLITE_BUSY`. Production: `SKIP LOCKED` removes
  head-of-line blocking.
- **Poll latency.** Mitigation: short `PollSeconds` for demos; production could add `LISTEN/NOTIFY`
  (Postgres) to push wakeups.
- **Unbounded backlog (schedule storm).** Mitigation: `MaxCatchUp` cap, per-definition concurrency,
  circuit breaker, priority aging; see `schedule-storm.md`.

## Alternatives not chosen

Dedicated broker (external infra, split source of truth), in-memory queue (not durable/multi-process).
The DB-as-queue pattern is a deliberate, well-understood trade-off: correctness and simplicity now,
with a documented, code-shaped path to `SKIP LOCKED` throughput later.
