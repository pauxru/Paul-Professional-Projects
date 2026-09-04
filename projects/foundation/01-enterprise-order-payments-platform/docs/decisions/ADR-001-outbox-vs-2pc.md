# ADR-001 — Transactional outbox vs two-phase commit

- Status: Accepted
- Date: 2025-11-24
- Deciders: @pauxru (solo)

## Context

Domain events (`OrderPlaced`, `PaymentAuthorized`, `PaymentCaptured`,
`PaymentFailed`, `RefundIssued`) must be published to downstream consumers
whenever the corresponding aggregate change persists. If the state change and
the publish happen in different resources, one of them will eventually fail:

- If we save the aggregate and then publish, a process crash between the two
  drops the event. Downstream systems miss it. **Silent lost update.**
- If we publish and then save, a save failure leaves consumers reacting to
  an event that never happened. **Ghost updates.**

We need atomicity across a state change and a message publish.

## Options

1. **Two-phase commit (XA / MSDTC).** The database and the broker enlist in a
   distributed transaction coordinator; the DTC decides the outcome. Works,
   but requires the broker to support XA, requires the app process to be a
   participant of the DTC, and doubles the failure surface.
2. **Transactional outbox.** Write the event to an `Outbox` table in the SAME
   transaction as the aggregate change. A separate dispatcher polls the table
   and publishes at-least-once. Duplicate delivery on retries — subscribers
   must be idempotent (see ADR-005).
3. **Event-carried state transfer without a durable log.** Fire-and-forget.
   Not viable — violates the required durability.
4. **CDC / logical decoding.** Postgres could stream WAL to Debezium and
   downstream consumers would react. Powerful, but requires Postgres and
   Debezium — outside the "runs against SQLite by default" contract.

## Decision

**We use the transactional outbox pattern (Option 2).**

- `OutboxMessage` rows include the topic, JSON payload, occurred-at,
  next-attempt-at, attempts count, dispatched flag, correlation id.
- `OutboxWriter` (Application layer) enqueues rows in the same DbContext used
  by the service. The service commits the transaction — state change + outbox
  are atomic.
- `OutboxDispatcher : BackgroundService` (Infrastructure) polls, publishes via
  `IEventBus`, retries with exponential backoff + jitter, and copies failed
  rows to `OutboxDeadLetters` after `MaxAttempts`.

## Consequences

- **Positive** — no distributed transaction; works on SQLite; the runbook is
  the "drain the outbox / re-drive the dead-letters" script.
- **Negative** — at-least-once delivery. Subscribers must dedupe; see ADR-005.
- **Operational** — a running dispatcher instance is required for events to
  flow. Health includes a `db` check but not "outbox drain rate" — a
  `outbox_dead_lettered_total` metric surfaces problems.

## Risks

- Dispatcher lag — mitigated by short poll interval + admin endpoint to run
  one dispatch cycle immediately.
- Poison messages — mitigated by dead-letter table + operator re-drive.
- Cross-instance duplication — mitigated in a real deployment by
  `SELECT … FOR UPDATE SKIP LOCKED`. On SQLite we only run a single dispatcher.

## Alternatives revisited

If we ever migrate to Postgres full-time, `LISTEN/NOTIFY` on outbox INSERTs
would replace the polling loop, reducing latency to essentially zero. That is
a straightforward substitution; the dispatcher interface is unchanged.
