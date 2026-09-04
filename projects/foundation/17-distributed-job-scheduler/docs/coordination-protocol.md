# Coordination Protocol — Lease, Heartbeat & Fencing

This document specifies the distributed-coordination protocol that is the headline of this
project. It is precise enough to re-implement against any store that supports a **conditional
update** (compare-and-set). SQLite is the default here; the same protocol maps directly onto
PostgreSQL, Redis, or etcd (see [Production mapping](#production-mapping)).

> **The core claim.** Correctness does **not** come from the database's isolation level. It comes
> from the **lease + fencing-token protocol**. SQLite is a single-writer store, which is fine and
> even instructive: it forces the coordination logic to be explicit rather than hiding behind row
> locks. The invariants below hold for any number of worker processes.

---

## 1. Vocabulary

| Term | Meaning |
|---|---|
| **Run** | One execution instance of a job definition (`JobRuns` row). |
| **Lease** | The right to execute a run, expressed as `(LeaseOwner, LeaseToken, LeaseExpiresAt)`. |
| **Lease token** | A per-claim `Guid`. Proves *who* currently holds the lease. |
| **Fencing token** | A per-run **monotonically increasing** `long` (`FencingToken`). Proves *how recent* a claim is. Bumped on every successful claim. |
| **Heartbeat** | A periodic conditional update that pushes `LeaseExpiresAt` forward while work is in flight. |
| **Reaper** | A leader-only sweep that returns expired-lease runs to `Pending`. |

The lease token answers *"do I still own this?"*. The fencing token answers *"has someone
superseded me since I started?"*. **You need both.** A lease token alone cannot stop a stalled
worker that wakes up believing it still holds the lease — only a monotonic fencing token compared
at the moment of write-back can.

---

## 2. State machine

```
Pending ──claim──▶ Claimed ──start──▶ Running ──success──▶ Succeeded
   ▲                  │                   │  │  └─────fail (budget left)──▶ Retrying ──▶ Pending
   │                  │                   │  └────────fail (exhausted)────▶ Failed ──▶ DeadLettered
   └──reaper (lease expired, no fencing bump)──┐      │
   └──retry re-arm──────────────────────────────┘     ├──timeout────▶ TimedOut ──▶ Retrying|DeadLettered
                                                       └──cancel─────▶ Cancelled
```

Terminal states: `Succeeded`, `Cancelled`, `DeadLettered`. `Failed`/`TimedOut` are transient book-keeping
states that immediately move to `Retrying`→`Pending` (budget remaining) or `DeadLettered` (exhausted).

---

## 3. Claim (compare-and-set, atomic)

A worker that finds a due run attempts to claim it with a single conditional `UPDATE`. In EF Core
this is `ExecuteUpdateAsync`; the emitted SQL is one statement, so it is atomic even though SQLite
serialises writers:

```sql
UPDATE JobRuns
SET State = 'Claimed',
    LeaseOwner = @node,
    LeaseToken = @token,          -- fresh Guid
    LeaseExpiresAt = @now + @lease,
    FencingToken = FencingToken + 1,   -- monotonic bump
    Version = Version + 1
WHERE Id = @runId
  AND State = 'Pending'
  AND ScheduledAt <= @now
  -- singleton definitions additionally require no active sibling:
  AND (@singleton = 0 OR NOT EXISTS (
        SELECT 1 FROM JobRuns o
        WHERE o.JobDefinitionId = JobRuns.JobDefinitionId
          AND o.Id <> JobRuns.Id
          AND o.State IN ('Claimed','Running')));
```

- **Exactly one winner.** With N workers racing the same `@runId`, the store serialises the
  writers; the first sets `State='Claimed'` and every subsequent update matches zero rows
  (`State` is no longer `Pending`). `rows affected == 1` means *you won*; `0` means *you lost*.
- **The fencing token is minted inside the same statement**, so the winner's token is strictly
  greater than any previously observed token for that run.
- Implemented in `JobRunStore.TryClaimAsync`.

## 4. Start & heartbeat

```sql
-- Start (Claimed -> Running), guarded by the lease token:
UPDATE JobRuns SET State='Running', StartedAt=@now, AttemptCount=AttemptCount+1
WHERE Id=@runId AND State='Claimed' AND LeaseToken=@token;

-- Heartbeat, extends the lease only while the token still owns it:
UPDATE JobRuns SET LeaseExpiresAt=@now + @lease
WHERE Id=@runId AND LeaseToken=@token AND State IN ('Claimed','Running');
```

The executor runs the heartbeat on its **own** database connection/scope in a background loop
(`RunExecutor.HeartbeatLoopAsync`) at `Engine:HeartbeatSeconds`, well inside the
`Engine:LeaseSeconds` window. If a heartbeat matches **zero** rows, the lease was lost (reaped and
possibly re-claimed elsewhere) — the executor stops touching the run and abandons its write-back.
The heartbeat read also surfaces a cooperative **cancel** request.

## 5. Complete (fencing-guarded write-back)

```sql
UPDATE JobRuns
SET State=@finalState, Output=@out, Error=@err, FinishedAt=@now,
    LeaseOwner=NULL, LeaseToken=NULL, LeaseExpiresAt=NULL, Version=Version+1
WHERE Id=@runId
  AND FencingToken=@fencing      -- the token minted when *I* claimed
  AND State='Running';
```

This is the safety property in one line. The write only lands if **my** fencing token is still the
current one **and** the run is still `Running`. If the run was reclaimed by another worker (which
bumped the token on its own claim) or already completed, my `UPDATE` matches zero rows and my result
is silently discarded. Implemented in `JobRunStore.TryCompleteAsync`.

## 6. Reaping (leader-only)

```sql
UPDATE JobRuns
SET State='Pending', LeaseOwner=NULL, LeaseToken=NULL, LeaseExpiresAt=NULL, Version=Version+1
WHERE State IN ('Claimed','Running') AND LeaseExpiresAt <= @now;   -- fencing token NOT bumped
```

The reaper deliberately **does not** bump the fencing token. The bump happens on the *next claim*.
This is what makes the stalled worker's late write fail on **both** guards: the state is no longer
`Running` (briefly `Pending`) and, once re-claimed, the token has moved on.

---

## 7. The failure case, proven

The scenario the fencing token exists for: a worker stalls (GC pause, a `SIGSTOP`, a network
partition to the store) past its lease. The leader reaps the run, a second worker reclaims and
completes it, and then the original worker wakes up and tries to write its now-stale result.

```mermaid
sequenceDiagram
    autonumber
    participant W1 as Worker A (stalls)
    participant DB as Store (JobRuns row)
    participant LDR as Leader (reaper)
    participant W2 as Worker B

    W1->>DB: claim run R  (CAS: Pending→Claimed, token=t1, fence=1)
    DB-->>W1: won (fence=1)
    W1->>DB: start (Claimed→Running, token=t1)
    Note over W1: begins work, then STALLS<br/>(no heartbeats)

    Note over LDR: lease age > LeaseSeconds
    LDR->>DB: reap: LeaseExpiresAt ≤ now → State=Pending<br/>(fence stays 1, lease cleared)
    DB-->>LDR: 1 run reclaimed

    W2->>DB: claim run R  (CAS: Pending→Claimed, token=t2, fence=2)
    DB-->>W2: won (fence=2)
    W2->>DB: start + heartbeat + complete<br/>(WHERE fence=2 AND State=Running)
    DB-->>W2: 1 row → Succeeded

    Note over W1: wakes up, tries to finish
    W1->>DB: complete R (WHERE fence=1 AND State=Running)
    DB-->>W1: 0 rows — FENCED OUT (current fence=2, State=Succeeded)
    Note over W1,DB: stale write rejected;<br/>state is not corrupted
```

**This exact sequence is a passing test.**
`LeaseReclaimTests.Expired_lease_is_reclaimed_and_the_stalled_workers_write_is_fenced_out` (and the
sibling claim-race / heartbeat tests) drive it against real file-backed SQLite with a `FakeClock`,
asserting: exactly one claim winner; the reclaim happens after lease expiry; the reclaiming worker
completes the run; and the stalled worker's fencing-guarded write returns **0 rows affected**.

---

## 8. Delivery semantics

The protocol provides **at-least-once** execution: a run can execute more than once (stall →
reclaim → the original *also* finishes its side effects before being fenced on write-back). Handlers
are therefore expected to be **idempotent**, keyed by the run's **idempotency key**
(`JobExecutionContext.IdempotencyKey`). The fencing token guarantees the *bookkeeping* is written at
most once (exactly-once state transition), but it cannot un-send a side effect a stalled handler
already performed — hence idempotent handlers are a first-class requirement, not an afterthought.
See `ADR-004-at-least-once-idempotent-handlers.md`.

---

## 9. Leader election (same idea, one row)

Leadership uses the identical lease pattern over a single `LeaderLeases` row:

- **Renew (fast path):** `WHERE Owner=@me AND Token=@myToken AND ExpiresAt>@now` → push `ExpiresAt`.
  Renewal does **not** bump the leader fencing token.
- **Take over:** `WHERE Owner IS NULL OR ExpiresAt<=@now` → set `Owner=@me`, `Token=@myToken`,
  `FencingToken = FencingToken + 1`, new `ExpiresAt`.

Exactly one node can win the take-over CAS. A paused old leader that resumes finds its token no
longer current (the new leader bumped the fence) and cannot perform singleton duties — split-brain
is avoided. Implemented in `LeaderElectionStore.TryAcquireOrRenewAsync`; proven by
`LeaderElectionTests` (single-leader invariant, failover within TTL, fence bump on takeover,
renew does not bump).

---

## Production mapping

| Concern | This project (SQLite) | PostgreSQL | Redis | etcd / ZooKeeper |
|---|---|---|---|---|
| Atomic claim | `UPDATE … WHERE State='Pending'` (single-writer) | `SELECT … FOR UPDATE SKIP LOCKED` then `UPDATE` | `SET NX` / Lua CAS on a per-job key | lease + txn compare on key revision |
| Lease TTL | `LeaseExpiresAt` column + reaper | same, or `pg_try_advisory_lock` | key TTL (`PX`) | native lease with TTL |
| Fencing token | monotonic `FencingToken` column | `xmin`/sequence or explicit counter | `INCR` fence key | key **ModRevision** (built-in monotonic) |
| Leader election | `LeaderLeases` row CAS | advisory lock / row CAS | Redlock (with caveats) | native leader election |
| Throughput ceiling | one writer (SQLite) | many writers via `SKIP LOCKED` | very high | moderate (control-plane, not data-plane) |

The application code (`IJobRunStore`, `ILeaderElectionStore`) is an abstraction; swapping SQLite for
Postgres is an infrastructure-layer change, not a protocol change. See
`ADR-002-db-as-queue-skip-locked.md` for the DB-as-queue trade-offs and the `SKIP LOCKED` mapping.
