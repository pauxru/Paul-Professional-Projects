# Security review — Contoso Payments (Project 01)

- Reviewer: @pauxru (author)
- Date: 2025-11-24
- Scope: everything in this repository. The application boundary is one API
  process + one SQLite database + the outbound provider simulator.

## Non-claims (honesty first)

- **This is not a certified system.** No PCI-DSS attestation, no SOC 2, no
  external penetration test.
- **No production secrets exist.** All keys in the repo are fake, obviously
  placeholder, and refuse to run in Production if unchanged.
- **The provider integration is a simulator.** There is no real cardholder
  data flow because there is no real vendor and no real customers.
- **Docker + Postgres stack is UNVERIFIED.** Whatever protections a
  production configuration would add via TLS/service mesh/managed identities
  are not exercised here.

## Threat model — STRIDE

The scope is: HTTP client → API → SQLite. A simulated payment provider posts
webhooks back to `/api/v1/webhooks/payments`.

| STRIDE | Threat | Mitigation | Confidence |
|---|---|---|---|
| **S**poofing | Attacker forges provider webhook to mark a payment captured. | HMAC-SHA256 over raw body; timestamp window; constant-time compare; replay-guard on hashed signature header. Test: `WebhookTests.Bad_signature_returns_401_and_does_not_mutate`. | High |
| **S**poofing | Attacker forges a bearer token. | JWT signature verified (`HS256`) using the configured signing key; issuer + audience checked. Refuses to boot in Production with default dev key. | Medium — depends on secret hygiene. |
| **T**ampering | Attacker replays a valid webhook. | Signature header hashed and stored in `WebhookReplayRecord`; second occurrence returns 200 with `replay: true` and does not mutate state. | High |
| **T**ampering | Attacker retries POST to duplicate a charge. | `Idempotency-Key` middleware; UNIQUE index on `(key, endpoint)`; response replayed byte-for-byte. Test: `OrderIdempotencyTests`, `PaymentFlowTests.Authorize_same_key_twice_only_charges_once`. | High |
| **R**epudiation | Actor denies having caused a state change. | `AuditEvent` table appended on every state-changing operation with actor, action, resource, correlation id, before/after hashes. | Medium — audit is local; no external notarisation. |
| **I**nformation disclosure | Response body of a prior request leaks via `Idempotency-Key`. | Replay is scoped to `(key, endpoint)`. A different endpoint with the same key does not replay. Tokens do not carry the key. | High |
| **I**nformation disclosure | Error response leaks internal detail. | ProblemDetails responses are hand-crafted; no unstructured `Exception.ToString()` is returned. | High |
| **D**enial of service | Attacker floods the API. | Framework rate limiter (fixed window, 200 requests / 10s / IP). Real deployments would front with a WAF. | Medium |
| **D**enial of service | Attacker sends huge request body. | Body size bounded by Kestrel defaults; idempotency middleware only buffers whitelisted routes. | Medium |
| **E**levation of privilege | Actor without `admin` scope creates a product. | JWT policy `admin` required on `POST /api/v1/products`. Test: `AuthAndProblemDetailsTests.Wrong_scope_returns_403`. | High |
| **E**levation of privilege | Actor without `reconciliation:run` scope triggers a run. | JWT policy `reconciliation:run` on that route group. | High |
| **E**levation of privilege | Anonymous request mutates state. | All write endpoints require authorization; webhook endpoint uses HMAC as its auth (`.AllowAnonymous()`). Test: `AuthAndProblemDetailsTests.Unauthenticated_POST_orders_returns_401`. | High |

## Concrete defences implemented

- **`SecurityHeadersMiddleware`** sets `X-Content-Type-Options: nosniff`,
  `Referrer-Policy: no-referrer`, `X-Frame-Options: DENY`.
- **`CorrelationIdMiddleware`** — every request receives a correlation id
  echoed to the client and included on every ProblemDetails and audit row.
- **`HmacSha256WebhookSignatureVerifier`** — the *only* signature check for
  the webhook endpoint; uses `CryptographicOperations.FixedTimeEquals`.
- **`Order.AddLine`** copies the `Money` reference to avoid EF Core tracking
  aliasing (defence in depth against value-object leakage between
  aggregates).
- **`OrderService` catches** `DomainException`, `DbUpdateConcurrencyException`,
  `DbUpdateException` — never surfaces a raw stack trace.
- **`Program.cs`** has a global try/catch middleware translating raw
  `SqliteException`/`DbUpdateException` to 409 ProblemDetails.

## Secrets handling

- `Jwt:SigningKey`, `PaymentProvider:WebhookSigningSecret`, and any DB
  connection strings are read from configuration only.
- `.env.example` documents the shape. `.env` is gitignored.
- `AuthSetup` in the API refuses to boot in Production if the JWT signing
  key is still the placeholder.
- Nothing in the repo is a real secret. Grep for the placeholders and you
  find the entire population.

## Known limitations

- No mTLS on the webhook endpoint. In a real deployment we would pin the
  provider's client certificate.
- No IP allow-listing on the webhook endpoint. Signature is the sole auth.
- The rate limiter is per-IP, so an attacker behind a NAT could DoS a
  co-located victim. Real deployments would add per-token limits.
- The audit trail is local. In a real system it would ship to an external
  log store with a hash chain.
- Rotating the JWT signing key requires a coordinated restart; there is no
  key-set-URL discovery.
