# Interview Talking Points

## 1. What business problem?

It replaces brittle point-to-point scripts with governed, versioned, observable integrations between CRM, ERP, and payment APIs.

## 2. Why non-trivial?

Correctness spans systems that cannot share a transaction. Auth, pagination, schema evolution, retries, partial progress, and human replay interact.

## 3. What can fail?

429/5xx/timeout, expired OAuth token, ETag conflict, invalid record, schema drift, process interruption, duplicate delivery, forged webhook, or leaked secret.

## 4. How does it recover?

Bounded retry honors vendor timing, circuits stop cascades, checkpoints preserve progress, poison records enter a DLQ, and replay reuses stable idempotency keys.

## 5. How is it secured?

JWT scopes, HMAC replay defense, SSRF allow-list/private-IP checks, AES-GCM secret storage, redaction, security headers, rate limits, and a non-Turing-complete expression evaluator.

## 6. How is it tested?

Unit tests pin language/security/reliability rules. `WebApplicationFactory` tests call real simulator endpoints and SQLite in memory without outbound traffic.

## 7. How is it observed?

Correlation IDs, run/step history, redacted snapshots, custom spans, latency/retry/run/record/DLQ metrics, and health probes.

## 8. Trade-offs?

SQLite and process-local scheduling maximize portability but limit horizontal scale. The small DSL sacrifices generality for safety.

## 9. How would it scale?

Partition work by flow/tenant, stream bounded pages, move state to PostgreSQL/SQL Server, use leases, externalize workers, and retain idempotency at the boundary.

## 10. What changes in an enterprise?

OIDC/workload identity, managed vault, approved egress proxy, durable scheduler coordination, centralized OTLP, vendor sandbox contract suites, and formal data-retention controls.
