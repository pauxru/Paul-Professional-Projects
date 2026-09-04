# Security Review — API Integration Hub

## Scope and method

This review covers the hub API, admin UI, flow/mapping engine, connector egress, local persistence, secret adapter, webhook ingress, and the three fictional simulators. It uses a design/code review and STRIDE analysis; it is not a penetration test.

## Assets

- Connector credentials and OAuth tokens
- CRM/ERP/payment payloads and PII-bearing fields
- Flow definitions and active-version state
- Idempotency results and checkpoints
- Run history, error context, and dead-letter payloads
- Webhook signing keys and consumed nonces
- Operator authorization scopes

## Trust boundaries

1. Browser/operator to hub API
2. External webhook sender to anonymous webhook endpoint
3. Hub to connector target over HTTP
4. Application services to SQLite
5. Hub process to encrypted secret file/master-key source
6. Operator replay action to target side effect

## Data classification

| Data | Classification | Handling |
|---|---|---|
| Credentials/tokens | Secret | References in config, AES-GCM local storage, never returned |
| Contact email/name/phone/address | Confidential PII | Redacted from snapshots |
| Payment amount/status | Confidential business data | Authenticated access, retention |
| Flow definitions | Internal configuration | Scoped write access, versions |
| Telemetry | Internal operational | Correlation and aggregate measurements; no credential tags |
| Fictional seed records | Synthetic | `.example` addresses and explicitly fictional organizations |

## Threat model (STRIDE per boundary)

| Boundary | S | T | R | I | D | E |
|---|---|---|---|---|---|---|
| Operator → API | Forged JWT | Flow mutation | Denied action disputed | History disclosure | Request floods | Scope escalation |
| Webhook → API | Forged sender | Body mutation | Replay | Error leakage | Webhook flood | Trigger protected flow |
| Hub → connector | DNS/host spoof | Response drift | Ambiguous write | Credential leak/SSRF | 429/timeout/5xx | Malicious endpoint |
| Hub → SQLite | Process impersonation | Row modification | History deletion | Snapshot exposure | Writer contention | DB-file access |
| Hub → secret file | Key spoof | Ciphertext modification | Rotation dispute | Key/value disclosure | Store unavailable | File permission abuse |
| Replay → target | Operator spoof | Payload alteration | Duplicate dispute | DLQ exposure | Replay storm | Unauthorized side effect |

## Mitigations implemented

### SSRF through connector URLs

- Absolute scheme check.
- Explicit host allow-list.
- DNS resolution followed by loopback/private/link-local/multicast/special-address rejection.
- Local simulator mode deliberately allows private localhost targets; production must disable that exception.
- Test proves a private destination and an allow-list miss are rejected.

### Credential leakage

- `@secret:` references are resolved only at execution time.
- AES-256-GCM authenticated encryption protects the local store.
- Master key can come from a dedicated environment variable.
- Redactor tracks resolved secret values and recursively masks sensitive field names.
- Secret metadata endpoints never return values.
- Tests assert ciphertext and stored run snapshots do not contain the plaintext secret.

### Expression sandbox escape

- No `eval`, compilation, reflection, process launch, filesystem, environment, or network APIs.
- Function identifiers must be ASCII letters/underscore and match a hard-coded switch.
- Expression length, recursion depth, result size, and mapping count are bounded.
- Tests submit CLR/file/process-shaped expressions and verify rejection.

### Webhook forgery and replay

- HMAC-SHA256 covers timestamp, nonce, and exact raw bytes.
- Fixed-time digest comparison.
- Configurable timestamp window (five minutes at the HTTP surface).
- Atomic one-use nonce store with expiry cleanup.
- Payload contract validation occurs after authentication.
- Dedicated webhook rate limit.

### Payload retention and PII

- Snapshots are redacted before persistence.
- History retention is configurable and enforced by a background worker.
- Page size and mapping payload limits reduce accidental unbounded capture.
- Production should add purpose-specific retention tiers and erasure workflows.

### Authorization and browser controls

- JWT signature, issuer, audience, and lifetime validation.
- Policies require explicit read/write/admin scopes.
- Default development signing key is rejected in Production.
- CORS uses configured origins.
- CSP, HSTS, frame denial, no-sniff, no-referrer, and permissions headers are present.

## Residual risk

- Scheduler overlap protection is process-local, so multiple hub replicas could overlap.
- The development token endpoint uses a static example client credential and must not be enabled in production.
- Local secret encryption cannot defend against an attacker who controls both the process and master-key environment.
- Redaction is defensive, not a data-loss-prevention proof; unknown PII field names may require organization-specific rules.
- JSON snapshots and DLQ payloads increase breach impact and should use database/storage encryption plus strict access control.
- A hostile allow-listed endpoint could return extremely expensive payload shapes; additional response byte limits should be configured at reverse proxy and handler layers.

## What would change for a real production deployment

- OIDC/JWKS validation through Entra ID or another identity provider; remove local token issuance.
- Managed secret vault with workload identity, audit, rotation events, and HSM-backed keys.
- TLS-only connector endpoints, certificate policy, egress proxy, and split-horizon DNS controls.
- PostgreSQL/SQL Server with encrypted backups, row-level operational permissions, and durable scheduler leases.
- WAF/API gateway webhook limits and vendor-specific signing schemes.
- Central OTLP telemetry with field filtering and restricted trace access.
- SAST, dependency scanning, DAST, threat-model sign-off, and provider contract testing.

## Explicit non-claims

This is a self-directed engineering demonstration. No formal security audit, penetration test, or compliance certification (PCI DSS, SOC 2, HIPAA, ISO 27001) has been performed or is claimed.
