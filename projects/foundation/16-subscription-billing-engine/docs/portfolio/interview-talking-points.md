# Interview Talking Points

## 1. What business problem?

It is an embeddable SaaS billing core, not checkout: it turns catalogue versions, subscription state, usage, discounts, credits, and tax into reproducible invoices and collection workflows.

## 2. Why non-trivial?

Historical version binding, month-end anchors, signed negative adjustments, time-ratio proration, aggregation semantics, tax rounding, payment reconciliation, and retry idempotency interact.

## 3. What can fail?

Duplicate invoice workers, late/replayed usage, rounding drift, gateway timeout, forged/replayed webhook, exhausted collection, outbound receiver failure, and database concurrency.

## 4. How does it recover?

Unique keys select one winner; immutable evidence permits replay/reconstruction; dunning uses deterministic retries; late payment webhooks recover access; outbound delivery dead-letters for operator replay.

## 5. How secured?

JWT scopes, HMAC raw-body verification, timestamp/nonces, request hashes, rate limits, startup secret guards, append-only audit records, no card data, and synthetic demo data.

## 6. How tested?

Boundary-heavy xUnit tests plus SQLite in-memory and `WebApplicationFactory` tests. The signature invariants are exact proration reconciliation, one invoice per period, and one usage event per ID.

## 7. How observed?

Correlation IDs, structured logs, readiness/liveness, OpenTelemetry traces, invoice duration, generated invoice, failed payment, and dunning counters.

## 8. Trade-offs?

SQLite and a modular monolith maximize local correctness and readability. Production multi-writer operation would add PostgreSQL worker leases and a real outbox transport without weakening unique constraints.

## 9. How scale?

Partition event/rollup data, batch ingestion, lease invoice work, separate destination retry budgets, and materialize analytics snapshots. Extract services only after measured boundaries justify distributed consistency costs.

## 10. Enterprise changes?

OIDC/JWKS, tenant isolation, managed secrets, PSP/Avalara adapters, database roles/backups, immutable ledger export, jurisdiction/product tax rules, SLOs, load tests, incident automation, and formal security/compliance work.
