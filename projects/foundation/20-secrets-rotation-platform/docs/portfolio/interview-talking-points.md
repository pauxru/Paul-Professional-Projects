# Interview Talking Points

## 1. What business problem does it solve?

It reduces the outage and exposure risk of rotating credentials shared by many applications. The
registry knows owners, consumers, versions, age, and policy; the engine coordinates migration
rather than assuming deployment and credential replacement are instantaneous.

## 2. Why is the architecture non-trivial?

Rotation is a long-running state machine with external side effects, explicit consumer evidence,
cryptographic key hierarchy, failure compensation, and privileged access controls. Each of those
concerns is behind an application-owned port.

## 3. What can fail?

Generation, persistence, notification, consumer refresh, downstream verification, operator
authorization, key availability, and process lifetime can all fail independently.

## 4. How does it recover?

Each state is persisted. Idempotency prevents duplicate requests. An interrupted engine resumes
from its last committed state. Missed acknowledgement and failed verification revoke the candidate
and preserve the known-good current version.

## 5. How is it secured?

Fresh DEKs, AES-256-GCM, metadata AAD, versioned wrapping keys, JWT scopes, path policies, strict
value-read rate limiting, append-only audits, redaction, HMAC notices, and four-eyes approvals.

## 6. How is it tested?

Unit tests attack the signature invariants directly: AAD transplant, ciphertext/tag tampering,
re-wrap, lifecycle windows, generators, state transitions, rollback, resume, jitter, RBAC, audit,
log leakage, and notification retries. HTTP tests prove validation, authentication, authorization,
health, OpenAPI, and real SQLite behavior.

## 7. How is it observed?

Correlation IDs flow through requests and audit. OpenTelemetry emits request traces plus rotation
outcome, value-read, acknowledgement-latency, and secret-age metrics. The dashboard exposes the
operator view.

## 8. What trade-offs were made?

SQLite and in-memory notification adapters maximize reproducibility. They deliberately avoid
pretending local infrastructure proves distributed behavior. Production would replace adapters,
not domain choreography.

## 9. How would it scale?

Move persistence to PostgreSQL/SQL Server, introduce compare-and-swap state transitions, lease
rotation work, persist a transactional outbox, partition workers by secret path, cache policy
decisions, and export telemetry centrally.

## 10. What changes in a real enterprise?

OIDC workload identity, managed HSM/KMS, private networking, durable queues/DLQ, immutable audit
storage, approval integration, per-consumer signing keys, migration tooling, backup drills,
security testing, and formally owned rotation SLOs.
