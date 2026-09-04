# ADR-001 — Lease + fencing token vs a distributed lock service

- **Status:** Accepted
- **Date:** 2026-09
- **Context tags:** coordination, correctness, headline

## Context

The scheduler must let multiple worker nodes claim and execute due jobs so that **exactly one node
runs a given run at a time**, while tolerating crashes, GC pauses, and network partitions. A naive
"lock the row, do the work, unlock" fails the moment a worker stalls: either the lock is held
forever (a stuck worker blocks the job), or the lock has a timeout and a *stalled* worker can wake
up after the timeout and clobber the state written by whoever took over. The build host has **no
Redis/etcd/ZooKeeper** and must run on SQLite alone.

## Options considered

1. **Distributed lock service (Redis Redlock / etcd / ZooKeeper).** Purpose-built, battle-tested
   leader/lock primitives. But: external infrastructure (violates the zero-infra prime directive);
   Redlock has well-known correctness caveats under clock skew/GC pauses; still needs a fencing
   token to be safe (Kleppmann's critique).
2. **Plain DB row lock / `SELECT … FOR UPDATE`.** Works within one transaction, but holding a
   transaction open for the entire job duration is an anti-pattern (connection pinning, long-lived
   locks, no heartbeat semantics), and SQLite has a single writer.
3. **Lease + monotonic fencing token via conditional updates (chosen).** Model ownership as data:
   `(LeaseOwner, LeaseToken, LeaseExpiresAt, FencingToken)` on the run row. Claim, heartbeat,
   complete and reap are each a single compare-and-set `UPDATE`. A per-run monotonic fencing token,
   compared at write-back time, rejects a stalled worker's late write.

## Decision

Adopt **option 3**. Correctness is a property of the protocol, not of the store's locking. The
fencing token is the crux: a lease/lock alone cannot make a stalled process safe, because the
process may resume *believing* it still holds the lease. Only a monotonic token, checked at the
moment the result is committed (`WHERE FencingToken=@token AND State='Running'`), guarantees that a
superseded worker's write is a no-op. The reaper deliberately does **not** bump the fencing token —
the bump happens on the next claim, so the stalled worker fails on both the state guard and the
token guard. Full protocol in [`coordination-protocol.md`](../coordination-protocol.md).

## Consequences

- **Positive:** No external infrastructure. Ownership is inspectable in the database. Heartbeat
  extends the lease so long jobs don't get falsely reclaimed. The exact stall→reclaim→fence sequence
  is unit/integration tested against real SQLite contention.
- **Positive:** The same pattern generalises to leader election (`ADR-002`, one row) and ports
  cleanly to Postgres/Redis/etcd (fencing token ↔ Postgres sequence / Redis `INCR` / etcd
  `ModRevision`).
- **Negative:** At-least-once, not exactly-once, execution — handlers must be idempotent
  (`ADR-004`). The application owns lease/TTL tuning (`LeaseSeconds` vs `HeartbeatSeconds`).

## Risks & mitigations

- **Clock skew across nodes.** Leases compare `LeaseExpiresAt` against each node's clock. Mitigation:
  all times are UTC via `IClock`; a conservative lease (30s) dwarfs expected skew; production would
  use a single DB clock (`now()` server-side) or NTP discipline.
- **Heartbeat starvation** (a busy handler blocks its heartbeat). Mitigation: the heartbeat runs on
  its **own** DI scope/connection in a background task, independent of the handler.
- **Lease too short → false reclaims.** Mitigation: `HeartbeatSeconds` (10) ≪ `LeaseSeconds` (30);
  validated by `HeartbeatOutcome` tests.

## Alternatives not chosen

Redlock (external + disputed safety), long-lived `FOR UPDATE` transactions (connection pinning), and
"just trust the lock timeout" (the exact bug the fencing token fixes).
