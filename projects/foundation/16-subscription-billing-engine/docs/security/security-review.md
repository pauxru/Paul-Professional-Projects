# Security Review — Subscription Billing & Usage Metering Engine

## Scope and method

This review covers the local API, financial domain rules, SQLite persistence, inbound payment webhooks, outbound webhook queue, authentication/authorization, and demo configuration. It is a design-time STRIDE review supported by automated tests; it is not a penetration test.

## Assets

- Plan versions, subscription state/anchors, usage evidence, invoices, credit notes, and account credits.
- Payment result metadata and dunning state. No PAN, CVV, bank credential, or payment secret is accepted.
- JWT and webhook signing keys supplied by configuration.
- Idempotency responses, webhook nonces, correlation IDs, and append-only audit hashes.
- Fictional customer names and operational metadata.

## Trust boundaries

1. SaaS/operator client to authenticated REST API.
2. Payment provider to anonymous-but-signed webhook endpoint.
3. Billing process to SQLite.
4. Background dispatcher to outbound webhook destinations.
5. Local configuration/secret source to process memory.

## Data classification

| Data | Classification | Notes |
|---|---|---|
| Invoice/usage/credit records | Confidential financial metadata | Synthetic in this repository; production data needs tenant controls and retention policy |
| Customer name/country | Business contact metadata | Demo values are fictional; avoid unnecessary PII |
| Payment method token | Sensitive token/reference | Simulator strings only; real tokens must be redacted and vault/provider scoped |
| Signing keys | Secret | Development placeholders are committed only in example/default config |
| Audit and correlation data | Internal operational | Protect from mutation and log injection |

## Threat model (STRIDE per boundary)

| Boundary | S | T | R | I | D | E |
|---|---|---|---|---|---|---|
| Client -> API | Stolen/forged JWT | Price, quantity, date, currency, or credit manipulation | Deny having changed subscription | Cross-customer invoice reads | Request/idempotency-key flooding | Read token used for admin operation |
| Payment -> webhook | Forged provider identity | Body changed after signing | Provider disputes notification | Error leaks invoice state | Replay/high-rate webhook traffic | Fake success recovers suspended account |
| Process -> DB | Wrong connection/file | Direct invoice/usage edits | Audit record deletion | Database copied | Locks/disk exhaustion | DB writer bypasses policy |
| Dispatcher -> destination | DNS/destination spoofing | Payload changed | Destination denies receipt | Financial payload exposure | Slow/failing receiver exhausts workers | Destination configuration changed |
| Config -> process | Rogue configuration source | Dev keys used in production | Secret rotation not recorded | Key logged | Invalid settings prevent startup | Overbroad identity/scopes |

## Mitigations implemented

### Billing manipulation

- Domain invariants re-check currencies, quantities, tier ordering, dates, state transitions, coupon limits, and monetary overflow.
- Plans are append-only versions; subscriptions bind exact versions.
- Proration accepts timestamps only inside the active period and reconciles every line to the intended net.
- Policy scopes separate `billing.read`, `billing.write`, and `billing.admin`.
- Subscription optimistic concurrency plus database uniqueness rejects conflicting writes.

### Webhook forgery and replay

- HMAC-SHA256 covers `timestamp.rawBody`, not reserialized JSON.
- Five-minute timestamp tolerance, constant-time digest comparison, required nonce, and unique nonce persistence.
- Invalid, expired, modified, and replayed webhooks are tested.
- Production should use independent rotated secrets per provider/endpoint and an authenticated secret store.

### Idempotency abuse

- Mutation keys are capped at 128 characters and scoped to HTTP method/path.
- A SHA-256 request hash prevents reusing a key with a different payload.
- Responses expire after 24 hours; state-changing operations still have domain/database uniqueness.
- Global fixed-window rate limiting limits unbounded key creation. Production should partition by tenant/client identity and periodically delete expired rows.

### PII and secret handling

- Seeded names are explicitly fictional and no cardholder data is modeled.
- `.env`, key files, local overrides, and SQLite files are ignored.
- Production startup refuses the known JWT key and any webhook secret prefixed `dev-only-`.
- Structured logs use IDs and classifications; payment tokens should be redacted in a real adapter.

### Financial audit trail

- Usage events, plan versions, credit notes, and audit records are append-only in EF save guards.
- Finalized invoice financial fields and all invoice lines are immutable; only lifecycle status may change.
- Audit entries include actor/source/action/resource/time and a SHA-256 state hash. A production ledger should add chained hashes, immutable storage, and privileged access monitoring.

### Additional controls

- Parameterized EF Core SQL, explicit indexes, bounded pagination, CORS allow-list, security headers, Problem Details, correlation IDs, health checks, and no HTTPS redirect dependency in the local host.
- Outbound webhooks are signed, bounded to five attempts, and moved to dead-letter state for operator replay.

## Residual risk

- The demonstration is single-tenant at the schema level; embedding SaaS code must enforce tenant identity with query filters and write assertions.
- Local HS256 token issuance is a Development/Testing convenience. Production requires OIDC/JWKS and managed service identities.
- SQLite file access can bypass application immutability. Production needs database roles, backups, encrypted storage, audit export, and separation of duties.
- Product taxability/nexus and payment disputes are materially more complex than the local adapters.
- The simulated outbound transport does not exercise DNS, TLS, proxy, SSRF, or response-size defenses.
- Availability controls have not been load-tested.

## What would change for a real production deployment

- OIDC issuer validation, short-lived tokens, tenant/scope claims, and authorization at both query and write paths.
- PostgreSQL/SQL Server with encryption, least-privilege roles, point-in-time recovery, migration gates, and worker leasing.
- Secret manager/HSM-backed key rotation and provider-specific webhook secret sets.
- PCI-scoped PSP tokenization with no card data traversing this service, plus provider reconciliation jobs.
- Outbound destination allow-lists, HTTPS-only validation, DNS/IP protections, mTLS where needed, timeouts/circuit breakers, and payload minimization.
- Immutable audit export/SIEM alerts for price, credit, refund, webhook, and privilege changes.
- Threat modeling for the actual tenancy, jurisdictions, vendors, data retention, incident response, and regulatory obligations.

## Explicit non-claims

This is a self-directed engineering demonstration. No formal security audit, penetration test, or compliance certification (PCI DSS, SOC 2, HIPAA, ISO 27001) has been performed or is claimed.
