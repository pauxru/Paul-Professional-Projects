# Security Review

## Scope

This document covers the notification platform's own attack surface. It
does **not** claim compliance with SOC 2, ISO 27001, PCI DSS, HIPAA, GDPR,
CCPA, or any similar regime. It is a design review for a self-directed
engineering case study.

## Explicit non-claims

- No SOC 2 audit was performed.
- No penetration test was performed.
- No real users, no real production data, no real provider credentials.
- Simulated providers only.

## Threat model — STRIDE

### Spoofing

| Threat | Mitigation |
|---|---|
| Attacker forges tenant credentials | JWT signed with a symmetric key; production guard refuses `dev-only-` keys. |
| Attacker forges a delivery receipt | HMAC-SHA256 over `timestamp + "." + body`; `X-Nonce` prevents replay; timestamp skew ≤ tolerance. |
| Attacker forges an unsubscribe link | HMAC-signed token; expiry enforced by injected clock. |

### Tampering

| Threat | Mitigation |
|---|---|
| Payload rewritten in transit | HTTPS at the edge (deployment concern); receipt HMAC covers body. |
| Template injection via unsafe payload | Rendering escapes HTML by default; `raw:` requires template-level opt-in; strict mode rejects unknown tokens. |
| Row-level data changed by another tenant | Every entity carries `TenantId`; every query filters on it; JWT scope carries the tenant id. |

### Repudiation

| Threat | Mitigation |
|---|---|
| Tenant claims "I never sent that message" | Every notification carries `CorrelationId`, `IdempotencyKey`, template version, and creation timestamp; every attempt is a `DeliveryAttempt` row. |
| Recipient claims "I never unsubscribed" | Unsubscribe events append a `SuppressionEntry(Reason=Unsubscribe)` with a timestamp and the redeeming token id. |

### Information disclosure

| Threat | Mitigation |
|---|---|
| PII in logs | Logs carry ids only; payloads are opaque JSON blobs stored on the notification, never logged. |
| Cross-tenant analytics leak | Analytics endpoints filter by the JWT tenant claim. |
| Provider credentials exfiltrated | No real credentials in this repo. Real deployment would use a secret store; refuse `dev-only-` keys in Production. |

### Denial of service

| Threat | Mitigation |
|---|---|
| One tenant floods the pipeline | Fairness scheduler bounds per-tenant slots per batch; hard quota per tenant caps monthly volume. |
| One provider misbehaves | Circuit breaker opens; failover to secondary. |
| Bulk API abused | `BulkMaxItems` cap; server enforces `BulkMaxItems` and rejects with 400. |

### Elevation of privilege

| Threat | Mitigation |
|---|---|
| Consumer of a low-scope token calls admin endpoints | Endpoints require explicit scope policies (`notifications:send`, `dlq:manage`, `receipts:ingest`, `analytics:view`, `templates:manage`, `preferences:manage`, `suppressions:manage`). |
| Server-Side Request Forgery through webhook channel | Simulator only in this repo. In a real deployment the outbound URL is validated against an allowlist / private-IP deny list before dispatch. This is a documented known limitation. |

## Specific concerns from the requirements

### Template injection / XSS

- Default output is HTML-escaped through `System.Net.WebUtility.HtmlEncode`.
- `{{ raw:field }}` is the only way to bypass escaping, and it requires the
  template to be created with `AllowRaw = true`.
- Strict mode rejects any unknown token — an attacker payload with
  `{{steal.token}}` is rejected instead of silently substituted.
- `TemplateEngineTests` covers escaping of `<script>` payloads, unknown
  tokens under strict mode, and the raw marker.

### SSRF via webhook channel

- The webhook channel is provider-simulated in this repo.
- A real deployment must validate outbound URLs against
  - private-IP / localhost / metadata-endpoint deny lists,
  - allow-listed domains per tenant,
  - a hop count bound to avoid redirect loops.

### Unsubscribe token forgery

- HMAC-SHA256 with the webhook signing key; verification uses
  `CryptographicOperations.FixedTimeEquals`.
- Payload includes recipient id, category, and expiry; changing any of them
  changes the signature.
- Tokens are URL-safe base64 and safe to embed in email templates.
- Tests: valid, tampered, expired.

### PII in logs

- Logs use structured properties for ids only.
- The rendered notification body is stored on the notification row but never
  logged.
- Recipient email/phone are stored on the recipient row (needed for
  delivery) and referenced by id in logs.

### Provider credential handling

- No real credentials exist in the repo.
- Simulators pull no secrets.
- Production guard on `JwtOptions.SigningKey` and
  `WebhookOptions.SigningKey` refuses to boot if the key starts with
  `dev-only-`.

## Residual risk

- Multi-instance safety of the outbox is not implemented (single-writer
  assumption). Fixing this requires a leased-worker registry or row-level
  locking. Documented in Known Limitations.
- Rate limits are in-process token buckets and do not survive restarts.
- The ops dashboard has no authentication.

## Review sign-off

Self-review by the author; no external security review performed.
