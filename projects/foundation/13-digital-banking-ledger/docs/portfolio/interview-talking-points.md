# Interview Talking Points — Example Bank Digital Banking Ledger

## 1. Why double-entry instead of a balance column?

A balance column is a cache; it is not the source of truth. In a financial system, every balance needs an explanation: which postings created it, when they happened, who requested them, and how corrections were made.

Double-entry accounting gives the system a conservation law. Every journal entry has at least two postings and must satisfy `Σ debits = Σ credits` per currency. That means a transfer, fee, reversal, interest accrual, or FX movement cannot be recorded unless the accounting equation still holds.

The ledger can expose fast balances, but the authoritative balance is derivable from immutable postings. That makes reconciliation, statements, audit trails, and error investigation much stronger than a mutable balance-only design.

## 2. How do you guarantee you cannot create money under concurrency?

The project combines domain invariants, locking, database transactions, optimistic concurrency, and idempotency.

At the domain level, unbalanced journal entries are rejected before persistence. At the application level, account locks are acquired before posting. The lock order is deterministic: account ids are sorted ascending and acquired in that order, so competing operations cannot form a circular wait. The global hash-chain lock is taken last so entry hashes remain linearized.

Persistence runs in serialized transactions and uses optimistic-concurrency `Version` columns. If a concurrent update wins first, the loser retries within a bounded retry policy. If the request is a duplicate retry with the same idempotency key, the system replays the existing result instead of posting again.

The behavior is tested with four concurrency tests: 150 parallel transfers between the same two accounts conserve total value, 150 bidirectional transfers do not deadlock, 50 withdrawals against limited funds do not overdraw, and 50 duplicate idempotency-key requests create exactly one posting.

## 3. Why `long` minor units instead of `decimal` or `double`?

`double` is inappropriate for money because binary floating point cannot exactly represent many decimal values. That creates rounding artifacts in precisely the domain where tiny differences matter.

This project stores money as integer minor units in a `long`, alongside explicit currency and scale. For example, KES, USD, and EUR amounts are represented as whole minor units. This makes addition, subtraction, equality checks, and invariant enforcement deterministic.

`decimal` is better than `double` for financial arithmetic, but the storage and invariant model still benefits from integer minor units because persisted money values are exact and scale-aware. Percentage calculations for fees, interest, and FX can round into minor units at explicit boundaries.

## 4. Walk me through your locking protocol and how you avoid deadlocks.

For any operation touching accounts, the application identifies the affected account ids and sorts them ascending. It then acquires per-account in-process locks in that exact order. Because every operation uses the same total order, two requests cannot each hold one lock while waiting for the other in the opposite order.

After all account locks are acquired, the system takes the global hash-chain lock. Taking the global lock last avoids making the hash-chain lock a bottleneck while waiting for account locks, and it preserves a single linear sequence for tamper-evident entry hashes.

The operation then runs inside a serialized database transaction. If optimistic-concurrency versions show that another transaction changed a row first, the operation retries within a bounded retry policy. This gives the design both deterministic in-process coordination and database-level conflict detection.

The limitation is that in-process locks are single-node only. In a multi-node deployment, the same logical protocol would need to move to database row locks, advisory locks, or another distributed coordination mechanism.

## 5. Optimistic vs pessimistic concurrency — why both?

They protect different failure modes.

The per-account locks are pessimistic coordination inside one process. They reduce avoidable conflicts, enforce a deterministic order, and prevent concurrent operations from interleaving account-level decisions such as available-balance checks.

Optimistic-concurrency `Version` columns protect the database boundary. They detect stale writes and conflicts that still matter even with application locks, especially as the design moves toward a production database or multiple execution paths.

Using both is a pragmatic belt-and-suspenders approach for a ledger: pessimistic locks shape the critical section, while optimistic concurrency verifies that persistence did not overwrite another valid change.

## 6. How does the tamper-evident hash chain work and what are its limits?

Each journal entry includes a SHA-256 hash that links it to the previous entry. The hash is computed from the entry content and the previous hash, creating a chain. If an old entry is modified, its hash no longer matches, and every downstream link becomes suspect.

The API exposes an integrity verification endpoint at `GET /api/v1/admin/integrity/verify` to check whether the chain is healthy.

The important limitation is that this is tamper-evident, not tamper-proof. If an attacker can rewrite the entire database and recompute every hash, the chain alone cannot stop that. In a real enterprise deployment, I would combine this with strict database permissions, immutable backups, audit logging, key management, and optionally external anchoring of hash checkpoints.

## 7. How does FX conversion stay balanced and conserve value?

FX conversion is modelled as accounting, not as a direct mutation of two balances. The conversion moves value through FX clearing accounts and uses a rounding gain/loss account when minor-unit rounding creates a remainder.

That keeps each side explicit: source currency movement, target currency movement, clearing, and rounding treatment are all represented as postings. The ledger can then prove that the journal entry balances per currency and that rounding has not silently created or destroyed value.

The system supports KES, USD, and EUR demo data. FX behavior is still a reference implementation; a real deployment would need provider integration, rate governance, approvals, and operational controls around rate changes.

## 8. How would this map to Postgres `SERIALIZABLE` / `SELECT FOR UPDATE`?

SQLite is the default so the project runs with zero external infrastructure. For Postgres, I would preserve the same conceptual protocol but push more coordination into the database.

Affected account rows would be selected in deterministic account-id order using `SELECT ... FOR UPDATE`, so the database holds row locks in the same deadlock-free order. The transaction isolation level would be `SERIALIZABLE` for ledger posting workflows that need strongest consistency. Serialization failures would be retried using the same bounded retry and idempotency strategy.

The hash-chain append could be protected by a single-row ledger-state lock, an advisory lock, or a dedicated sequence/anchor row selected `FOR UPDATE` after the account locks. The key is to keep the lock order stable: account locks first in ascending order, then the global chain lock.

## 9. What are the security controls?

The API uses JWT bearer authentication with scope-based policies:

- `ledger:read`
- `ledger:post`
- `ledger:adjust`
- `ledger:admin`

Errors are returned as ProblemDetails. Requests can carry correlation ids for traceability. Posting endpoints use the `Idempotency-Key` header. Append-only behavior is enforced in the DbContext so journal history is not updated destructively.

The honest limitations are that the demo uses a single symmetric signing key, and there is not yet a four-eyes maker-checker workflow. Adjustments require `ledger:adjust`, but dual approval is not enforced.

## 10. What did you test?

The real result is 99 passing tests on .NET 10 using `dotnet test -c Release`: 80 unit tests and 19 integration tests, 0 failed.

The tests cover domain invariants, accounting rules, API behavior, authorization, idempotency, concurrency, holds, reversals, fees, interest, FX, statements, trial balance, hash-chain integrity, and reconciliation behavior.

The signature tests are the concurrency cases because they test the system property that matters most: concurrent requests must not create money, overdraw accounts, deadlock, or double-post a retried request.

## 11. What are the limitations / what would you do next?

This is a self-directed engineering case study, not a deployed banking system. It does not claim production traffic, uptime, real users, compliance certification, or client impact.

Technical limitations:

- SQLite is the default persistence store for local reproducibility.
- Docker configuration was not verified because Docker is unavailable on the build host.
- In-process locks work for a single-node runtime only.
- JWT signing uses a single symmetric demo key.
- No four-eyes maker-checker workflow is enforced yet.
- The FX implementation is a reference implementation, not an integration with a live rate provider.
- The hash chain is tamper-evident but not a full immutable audit infrastructure by itself.

For a real production path, I would prioritize:

1. Move posting workflows to Postgres with `SERIALIZABLE` transactions and deterministic `SELECT FOR UPDATE` row locking.
2. Add maker-checker approval for adjustments, reversals above thresholds, and operational account changes.
3. Replace the demo symmetric signing key with production identity provider integration and proper key rotation.
4. Add external anchoring for hash-chain checkpoints.
5. Add operational runbooks for reconciliation failures, stuck holds, retry exhaustion, and suspicious integrity verification results.
6. Expand observability dashboards around posting latency, rejected imbalance attempts, idempotency replays, hold expiry, and reconciliation drift.
7. Add performance/load tests clearly labelled as synthetic benchmarks if throughput numbers are needed.

## 12. Short Closing Pitch

This project shows that I can design a backend around invariants, not just endpoints. The main engineering value is the combination of double-entry accounting, append-only corrections, idempotent APIs, concurrency control, tamper-evidence, clean architecture, and tests that attack the failure modes most likely to break financial systems.
