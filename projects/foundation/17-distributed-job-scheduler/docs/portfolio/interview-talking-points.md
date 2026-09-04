# Interview Talking Points — Distributed Job Scheduler & Orchestrator

Use these as concise Q&A prompts for discussing the design as a self-directed case study.

## 1. Coordination protocol: lease plus fencing token

### Q: How does a worker claim a job without a distributed lock service?

**A:** A worker first reads eligible due candidates, then wins ownership only through a conditional database update: the run must still be `Pending` and due. A successful update records `leaseOwner`, a fresh `leaseToken`, and `leaseExpiresAt`, changes the state to `Claimed`, and increments a monotonic fencing token. Concurrent workers can inspect the same candidate, but only one conditional update wins.

### Q: Why is a lease alone insufficient?

**A:** A lease tells the system when ownership should expire, but it does not by itself stop an old process from writing after it wakes up. Fencing turns ownership into an ordered epoch. Every new claim has a larger token, and completion requires both the worker's token and `State=Running`; an earlier epoch cannot commit over a later one.

### Q: What exact failure sequence does fencing prevent?

**A:**

1. Worker A claims run R with token `41`, starts it, and then stalls.
2. A stops heartbeating; its lease expires.
3. The leader reaps R to `Pending`.
4. Worker B claims R and receives token `42`, then starts or completes it.
5. A resumes and tries its old completion with token `41`.
6. The completion update uses `WHERE FencingToken=@token AND State=Running`, so token `41` no longer matches `42`; it affects zero rows and cannot overwrite B.

The protocol therefore accepts that work may execute again, but prevents stale ownership from corrupting the final scheduler state.

## 2. Why a database-backed queue, and how would it map to production systems?

### Q: Why use the database as the queue in this project?

**A:** Job definitions, schedules, runs, leases, logs, and recovery state share one durable source of truth. That keeps a local demo self-contained—EF Core 10 with SQLite and the .NET SDK are enough—and lets the claim/reap lifecycle be transactional with the run record.

### Q: What is the SQLite trade-off?

**A:** SQLite's single-writer behavior is useful for a portable default and for exposing contention behavior clearly, but it limits write scalability and is not positioned as a horizontally scalable production data plane.

### Q: How does the design map to PostgreSQL, Redis, or etcd?

**A:** These are documented production mappings, not alternate implementations in this case study:

- **PostgreSQL:** use `SELECT … FOR UPDATE SKIP LOCKED` to distribute candidate selection among workers, while retaining lease expiry, fencing, and guarded final writes.
- **Redis:** use an atomic script and TTL for claim/lease operations, while deliberately designing where durable run history and recovery live.
- **etcd:** use leases and compare-and-swap transactions for coordination/leadership epochs; it does not automatically replace durable job/run history.

The invariant to preserve is not a particular database statement; it is “only the current fenced owner may advance or complete a run.”

## 3. Leader election and split-brain avoidance

### Q: What needs a leader when workers can claim independently?

**A:** Claiming is distributed, but some fleet-wide duties must run once: schedule materialisation, reaping expired run leases, dead-node detection, and retention pruning. A lease-based leader election over one database row assigns those singleton duties.

### Q: How does the leader lease work?

**A:** A node conditionally acquires or renews the row using a TTL and heartbeat. Renewing the current lease preserves its epoch; a vacant or expired lease is taken over with a new owner token and a bumped fencing token.

### Q: How does that address split brain?

**A:** A pause can make an old leader believe it was leader even after its TTL elapsed. The takeover creates a newer fenced epoch. A node performs singleton work only while it holds the current lease, and the higher token identifies the valid ownership generation after takeover.

## 4. Cron, timezone, and DST edge cases

### Q: Why write a cron parser rather than treat schedules as strings?

**A:** The application needs to calculate real next occurrences, validate input, and test edge cases. The parser supports five- and six-field expressions, ranges, steps, lists, names, `L`, `#`, and the Vixie day-of-month/day-of-week OR rule.

### Q: What are the DST failure modes?

**A:** Local wall-clock time is not continuous. A spring-forward transition contains skipped local times; a fall-back transition repeats local times. Schedule calculation is timezone-aware and tests those transitions as UTC occurrences instead of assuming a fixed “one local day later” interval.

### Q: What cron cases are worth calling out in an interview?

**A:** A useful example is a schedule whose local time is skipped in spring, followed by the same expression on the repeated fall-back hour. Another is validating that day-of-month and day-of-week follow the Vixie OR rule rather than an intuitive but incompatible AND interpretation.

## 5. At-least-once execution and idempotency

### Q: Is execution exactly once?

**A:** No. Lease expiry deliberately allows the scheduler to re-run work after a worker failure, so the delivery guarantee is at least once. The system prevents stale state writes with fencing; it cannot prove an external side effect happened only once if a process dies at the wrong time.

### Q: How are duplicate effects handled?

**A:** A trigger carries an idempotency key, and the scheduler rejects a duplicate key at submission time. The key is also available to execution logic, so integrations should use it when making external effects idempotent. This separates correct scheduler ownership from the application-specific semantics of an email, payment, export, or API call.

## 6. Testing concurrency deterministically

### Q: How did you make timing-sensitive tests reliable?

**A:** A `FakeClock` makes time-dependent behavior deterministic: due times, lease expiry, heartbeats, retries, cron occurrences, and leader TTLs can move forward without sleeping. Tests use hard `CancellationToken` timeouts so a deadlock or regression fails rather than hanging the suite.

### Q: Why test against real file-backed SQLite as well?

**A:** In-memory substitution cannot demonstrate the actual writer contention or conditional-update behavior of the deployed default store. File-SQLite integration tests exercise competing contexts/process-like contention and validate that the database remains the final arbiter of a claim.

### Q: What is the verified test result?

**A:** The verified result is **186 tests, 0 failed, 0 skipped**: **155** in `JobScheduler.UnitTests` and **31** in `JobScheduler.IntegrationTests`.

## 7. Trade-offs and production-scale changes

### Q: What is the principal trade-off in the default implementation?

**A:** It optimizes for a zero-infrastructure, inspectable case study. SQLite makes local setup simple but serializes writers, so it is appropriate for the default demo rather than a high-throughput, highly available production scheduler.

### Q: What would change first for production scale?

**A:** Move the durable store to a highly available production database and use the documented PostgreSQL `SKIP LOCKED` claim-selection pattern or another implementation that preserves the same lease/fencing invariants. Then tune lease and heartbeat durations for real job profiles, partition or shard load where warranted, and validate recovery behavior under production contention.

### Q: What would not be compromised during that migration?

**A:** The clean boundaries remain useful: Domain stays independent of infrastructure, Application owns ports, and Infrastructure supplies the storage/coordination adapter. The security posture also remains: use a real identity provider in Production, keep the development token endpoint disabled there, reject the default signing key, and continue to allow only registered handler types rather than executing payload-provided code.

### Q: Is Docker part of the validated production story?

**A:** No. The Dockerfile and compose file are authored but unverified because Docker was unavailable on the build host; the compose stack has not been started or verified.

