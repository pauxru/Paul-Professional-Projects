# ADR-003 — Ordered locking *and* optimistic concurrency, not optimistic-only

- **Status**: Accepted
- **Date**: 2026
- **Context tags**: concurrency, correctness, deadlock

## Context

The ledger must stay correct under real parallel load: concurrent transfers, withdrawals against
limited funds, and duplicate submissions. Two families of concurrency control are available:

- **Optimistic** — read a version, do work, write with a version check; retry on conflict.
- **Pessimistic** — take a lock on the contended resource before doing work.

A ledger posting is multi-row (two accounts + postings + a hash-chain append). Under optimistic-only
control, a hot account (e.g. a cash or a popular customer account) produces a storm of version
conflicts and retries, and the funds check is only validated *after* the fact — the invariant
"available never goes below the overdraft floor" is awkward to prove when writers race and retry.
Pessimistic locking, done carelessly, deadlocks (`A→B` vs `B→A`).

## Options considered

1. **Optimistic-only** (version columns + retry).
2. **Pessimistic-only** (lock accounts; risk deadlock).
3. **Ordered pessimistic locking + optimistic version columns + serialized append + bounded retry**
   (defence in depth).

## Decision

Adopt option 3. Before mutating, acquire a per-account lock for **every** account the command
touches, always in **ascending account-id order**, then take a **global chain lock** last for the
hash-seal step. Keep **optimistic `Version` tokens** on `accounts`/`holds`/`interest_accruals` as a
second line of defence, run each command in its own transaction, and **retry** transient/optimistic
failures up to five times. A client idempotency key (unique index + stored response) makes duplicates
post exactly once.

## Consequences

**Positive**
- **Deadlock-free by construction**: a single total lock order means the wait-for graph is acyclic —
  proven by the bidirectional-transfer test, not merely mitigated by a timeout.
- **Overdraw is impossible**: the funds check and the balance mutation happen under the source
  account's lock, so no two withdrawals pass the check against the same balance — proven by the
  concurrent-withdrawal test.
- **Belt and braces**: even if a lock is mis-scoped or a second process appears, the version tokens
  turn a lost update into a retryable conflict rather than silent corruption.
- The protocol maps cleanly onto Postgres `SELECT … FOR UPDATE` in id order + `SERIALIZABLE` retry
  (see ADR-005).

**Negative / costs**
- The global chain lock **serializes writes** — throughput is intentionally traded for correctness.
- In-process locks are **single-node** only (see Risks).

## Risks and mitigations

- **Risk**: in-process locks don't coordinate across multiple API instances. **Mitigation**: this is
  documented as a known limitation; the Postgres mapping (ADR-005) replaces them with row locks /
  `SERIALIZABLE`. The `Version` tokens already provide cross-process safety against lost updates.
- **Risk**: write throughput bottleneck at the chain lock. **Mitigation**: acceptable for a
  correctness-first reference implementation; a production system could shard or relax the strict
  total-order chain.
- **Risk**: ret/replay masking a real error. **Mitigation**: only `TransientStorageException` and
  `ConcurrencyConflictException` are retried; everything else propagates as a `ProblemDetails`.

## Alternatives not chosen

- *Optimistic-only* — rejected: conflict storms on hot accounts and an awkward funds-check proof.
- *Pessimistic-only without ordering* — rejected: deadlocks under `A→B` / `B→A`.
- *A distributed lock (e.g. Redis)* — rejected: adds external infrastructure the project explicitly
  avoids; unnecessary for the single-node default.
