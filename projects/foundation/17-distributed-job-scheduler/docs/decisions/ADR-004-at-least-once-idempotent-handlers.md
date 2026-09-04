# ADR-004 — At-least-once delivery + idempotent handlers

- **Status:** Accepted
- **Date:** 2026-09
- **Context tags:** delivery-semantics, correctness, handlers

## Context

A distributed scheduler with crash recovery must choose a delivery guarantee. True exactly-once
execution of arbitrary side effects is impossible in the presence of process crashes between "did
the work" and "recorded that I did the work". The lease/fencing protocol (`ADR-001`) guarantees
exactly-once *state transition* (the bookkeeping `UPDATE` lands at most once), but a stalled worker
may already have performed a side effect before being fenced out on write-back.

## Options considered

1. **At-most-once.** Never retry; drop on any doubt. Unacceptable — jobs would silently not run.
2. **Exactly-once (distributed transactions / 2PC across handler side effects).** Requires every
   side effect to enlist in a distributed transaction with the scheduler DB. Impractical for
   heterogeneous handlers (files, HTTP, other DBs) and forbidden infra-wise.
3. **At-least-once + idempotency keys (chosen).** Guarantee the job runs *at least* once; make
   re-runs safe by giving every attempt a stable idempotency key.

## Decision

Adopt **at-least-once** semantics and make **idempotent handlers a first-class contract**.

- Each run carries a stable `IdempotencyKey`, surfaced to handlers via `JobExecutionContext`.
  Schedule materialisation and DLQ replay dedupe on it (`UNIQUE(IdempotencyKey)` in the schema),
  so a given logical occurrence is materialised once even if the leader runs the materialiser twice.
- Handlers are expected to use the key to guard their side effects (upsert by key, check-then-act,
  or a downstream idempotency token). The sample `CleanupHandler`/`ReportGeneratorHandler`
  demonstrate keying behaviour.
- The fencing token ensures the *scheduler's* record of the outcome is written exactly once; the
  idempotency key ensures the *handler's* side effect is safe when the run executes more than once.

## Consequences

- **Positive:** Crash/stall recovery is safe: a reclaimed-and-re-run job converges to the same
  observable result. No lost jobs. No distributed transactions.
- **Positive:** Testable — `RetryDlqTests` proves retry→success and retry-budget→dead-letter→replay;
  the reclaim/fencing tests prove the stale write is rejected while the reclaiming worker completes.
- **Negative:** Handler authors carry a real obligation. A non-idempotent handler (e.g. "send email"
  without a dedupe key) can double-fire its side effect on reclaim. This is documented in the
  security review and the handler contract.
- **Negative:** Idempotency keys must be chosen well (stable across retries, unique across logical
  occurrences); a bad key either blocks legitimate runs or fails to dedupe.

## Risks & mitigations

- **Non-idempotent handler double-effect.** Mitigation: the contract and docs make idempotency a
  requirement; the DLQ + replay flow encourages fixing the handler rather than blindly retrying;
  timeouts/cancellation reduce the window.
- **Idempotency-key collisions across distinct occurrences.** Mitigation: keys embed definition id +
  occurrence discriminator; the unique index surfaces collisions loudly rather than silently
  merging.
- **Poison jobs re-running forever.** Mitigation: `MaxAttempts` + poison detection → dead-letter;
  per-definition circuit breaker stops a failing type from consuming the fleet.

## Alternatives not chosen

At-most-once (drops work), exactly-once via 2PC (impractical, infra-heavy, doesn't cover external
side effects). At-least-once + idempotency is the industry-standard pragmatic choice and is what the
fencing protocol is designed to support.
