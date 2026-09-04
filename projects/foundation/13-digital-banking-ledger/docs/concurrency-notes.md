# Concurrency Notes — Example Bank Digital Banking Ledger

This is the most important document in the repository, because concurrency is where ledgers create
money by accident. It describes the concurrency model, why it is correct, the measured results of the
four concurrency tests, and how the model maps onto Postgres.

## The threat

A ledger posting is inherently **multi-row**: a transfer reads two accounts, checks funds, writes
two postings, updates two cached balances, and appends one hash-chained entry. If two such
operations interleave without coordination, classic anomalies appear:

- **Lost update / money creation** — two withdrawals both read balance 100, both post 80, and the
  account ends at −60. The bank invented 60 units.
- **Write skew** — two operations each individually respect an invariant but jointly violate it.
- **Deadlock** — `A→B` locks A then waits for B while `B→A` locks B then waits for A.
- **Duplicate submission** — a retried request posts twice.

## The model

The engine combines four mechanisms, each defending a different flank. All of them live in
`LedgerCommandExecutor` and `AccountLockManager`.

### 1. Ordered per-account locking (deadlock freedom)

Before any mutation, the executor acquires an in-process `SemaphoreSlim` for **every account the
command touches**, and it always acquires them in **ascending account-id (GUID) order**:

```csharp
var ordered = accountIds.Distinct().OrderBy(id => id).ToList();
foreach (var id in ordered) await _accountLocks.GetOrAdd(id, _ => new SemaphoreSlim(1,1)).WaitAsync();
```

Because there is a single, total acquisition order shared by every command, the lock wait-for graph
can never contain a cycle, so **deadlock is impossible by construction** — not by timeout. `A→B` and
`B→A` both lock `min(A,B)` first, so one simply waits for the other.

### 2. Global chain lock (append serialization)

The hash chain is a strict total order (`hash = SHA-256(previousHash ‖ content)`, `sequence =
head+1`). The seal step is therefore performed under a **single global chain lock**, taken *after*
the per-account locks. This both keeps the chain a clean total order and makes SQLite's single-writer
model safe: only one writer is ever sealing/committing at a time.

### 3. Optimistic concurrency tokens (defence in depth)

`accounts`, `holds` and `interest_accruals` carry a `Version` column marked `IsConcurrencyToken()`.
Even though the in-process locks already serialize writers within one process, the version column
catches any lost update that would arise from a second process or a mis-scoped lock, converting it
into a `ConcurrencyConflictException` that the executor retries.

### 4. Serialized transaction + bounded retry + idempotent replay

Each command runs in its **own** `DbContext`/transaction (via the unit-of-work factory), so parallel
commands never share EF change-tracking state. Transient storage errors and optimistic conflicts are
retried up to **5 times** with a small backoff. A client `Idempotency-Key` is checked before locking
and is backed by a **unique index**; a racing duplicate insert raises `DuplicateKeyException`, which
the executor turns into a replay of the stored response — so duplicates post **exactly once**.

### Total lock order

```
per-account locks (ascending id)  ─▶  global chain lock  ─▶  commit
```

## Why this is correct

- **No overdraw**: the funds check and the balance mutation happen while the source account's lock is
  held, so no two withdrawals can both pass the check against the same balance.
- **No deadlock**: single total lock order.
- **No money creation**: every entry is balanced (domain invariant) and appended atomically; a
  rollback leaves the books balanced.
- **Exactly-once**: unique idempotency index + replay.
- **Detectable drift**: the integrity endpoint reconciles cached balances against the derived
  balances and verifies the hash chain, so even a hypothetical bug is observable rather than silent.

## Measured results

The four concurrency tests in `tests/ExampleBank.Ledger.IntegrationTests/ConcurrencyTests.cs` run the
**full HTTP + concurrency stack** against a file-backed SQLite database. Latest run
(`dotnet test -c Release`, all passing; the four tests complete in ≈5 s wall-clock on the development
machine, including host startup):

| Test | Load | Assertion | Result |
|------|------|-----------|--------|
| `ParallelTransfers_SameTwoAccounts_ConserveTotalAndDeriveCorrectly` | 150 parallel `A→B` transfers of 500 minor; A funded 1,000,000 | A = 925,000; B = 75,000; **A+B = 1,000,000** conserved; integrity healthy | ✅ pass |
| `BidirectionalTransfers_DoNotDeadlock_AndConserveTotal` | 75 `A→B` + 75 `B→A` transfers of 1,000; both funded 500,000 | all 150 complete in < 60 s (no deadlock); **A+B = 1,000,000** | ✅ pass |
| `ConcurrentWithdrawals_AgainstLimitedFunds_NeverOverdraw` | 50 parallel withdrawals of 10,000; only 100,000 available, overdraft 0 | **exactly 10 succeed**, 40 rejected 422; source ends at 0, **never negative** | ✅ pass |
| `ParallelDuplicateIdempotencyKeys_ProduceExactlyOnePosting` | 50 parallel transfers sharing one idempotency key | **exactly one** entry id observed; source moved once (95,000) | ✅ pass |

These four assertions are the operational definition of "it is impossible to create money": totals
are conserved, funds are never overdrawn, symmetric flows do not deadlock, and duplicates post once.

## Mapping to Postgres (`SERIALIZABLE` / `SELECT … FOR UPDATE`)

The in-process locks are an artefact of the **single-node, single-writer SQLite** default. On
Postgres the same guarantees come from the database itself, and the application-level lock manager
would be removed:

| This project (SQLite, single node) | Postgres (multi-node) |
|------------------------------------|------------------------|
| `AccountLockManager` per-account `SemaphoreSlim`, ascending-id order | `SELECT … FOR UPDATE` on the account rows, issued in ascending id order to preserve deadlock freedom |
| Global chain lock around the seal | An advisory lock (`pg_advisory_xact_lock`) or a `SERIALIZABLE` transaction over the `journal_entries` sequence, or an append-only outbox with a single sequence generator |
| Bounded retry on transient/optimistic errors | Retry on `40001 serialization_failure` / `40P01 deadlock_detected` — the standard `SERIALIZABLE` retry loop |
| `Version` optimistic tokens | Kept as-is (also works on Postgres), or superseded by `SERIALIZABLE` |
| Unique index on idempotency key | Identical — the unique constraint is the real guarantee |

### How SQLite differs (honest note)

SQLite is a **single-writer** database: at most one write transaction commits at a time
(`busy_timeout` is set to 5 s and `foreign_keys=ON` via a connection interceptor). That actually makes
serialization *easier* to reason about than Postgres MVCC — but it also means the in-process locks
are only correct for **one process**. Running two API instances against the same SQLite file would
not be coordinated by the `AccountLockManager`. Postgres removes that limitation: row locks and
`SERIALIZABLE` isolation coordinate across every connection and node, at the cost of a real
serialization-failure retry loop (already modelled here by the executor's retry on
`ConcurrencyConflictException`). The application code path — take account locks in id order, validate,
append atomically, retry on conflict, replay on duplicate key — is deliberately identical in shape to
what the Postgres version would do.
