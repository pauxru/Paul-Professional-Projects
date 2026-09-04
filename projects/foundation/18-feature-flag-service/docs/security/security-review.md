# Security Review — Feature Flag & Dynamic Configuration Service

## Scope and method
This review examines the .NET API, SQLite control-plane data, SSE configuration transport, the local-evaluation SDK, development admin UI, and demo consumer. It uses design inspection and automated authorization/configuration tests; it is not a formal assessment.

## Assets
| Asset | Sensitivity | Primary protection |
|---|---|---|
| Server SDK keys | high | never returned in configuration; server-side storage only in this demo |
| Client SDK keys | medium | environment scoping and client-side flag filtering |
| Flag rules/JSON values | medium/high | SDK key authorization; `ClientSide` exposure gate |
| Private context attributes | high | SDK keeps them out of buffered events |
| Audit entries | high integrity | append-only application path with actor/correlation/before/after/diff |
| JWT signing material | high | config-bound development placeholder; Production startup guard |

## Trust boundaries
1. Browser operator to JWT-protected admin API.
2. Consumer process to SDK, then SDK-key-authenticated API bootstrap/SSE/events endpoints.
3. API/application layer to SQLite persistence and in-process broadcaster.
4. Development-only token issuer to local operators; production must use OIDC/JWKS.

## Data classification
All seeded names are fictional. No real personal data or real credentials are included. Feature values can be confidential when they describe unreleased behavior; server-only JSON flags must never be marked client-side.

## Threat model (STRIDE per boundary)
| Boundary | S | T | R | I | D | E | Mitigations |
|---|---|---|---|---|---|---|---|
| Operator → API | forged token | change payload | denied change | audit leakage | request flood | writer acting as approver | JWT validation, scope policies, validation, four-eyes checks, correlation/audit, rate limits |
| SDK → config/SSE | stolen SDK key | altered response | ambiguous update | client config leak | reconnect storm | client key reading server flags | per-environment keys, client/server response filtering, HTTPS required in deployment, backoff, polling |
| SDK → events | spoofed analytics | event tampering | unreliable telemetry | private attributes | oversized queue | event endpoint abuse | bounded buffer/drop counter, SDK key validation, no attributes in event DTO, rate limit |
| API → SQLite | local process spoofing | database tampering | audit alteration | snapshot leakage | lock/contention | direct DB privilege | least-privilege filesystem/account in deployment, backups/encryption outside demo, append-only application API |
| Regex rules → evaluator | n/a | malicious pattern | n/a | n/a | regex DoS | n/a | 256-char pattern cap, 4096-char input cap, 50ms timeout, `NonBacktracking` regex mode |

## Mitigations implemented
- `X-Sdk-Key` configuration endpoints distinguish client and server keys; client keys only receive `ClientSide` flag definitions.
- Private attributes are a property of `EvaluationContext` but are intentionally absent from `SdkEventDto`.
- JWT policies protect read/write/approve surfaces; the production startup guard rejects the development-only signing key.
- Production requests enforce separate requester/reviewer identities. Kill-switch bypass is explicitly audited, not hidden.
- RFC 7807 errors, correlation IDs, security headers, CORS allow-list, and partitioned fixed-window rate limits are present.
- SSE authorization occurs before the stream starts. SDK reconnection backs off and conditional polling prevents an invalidation event from becoming a config leak.
- SQL access uses EF Core parameterization. No raw user-derived SQL is used.

## Residual risk
SQLite stores demo SDK keys in plaintext, SSE fan-out is local-process only, exact analytics retention is unbounded, and client SDK keys are bearer credentials that cannot be made secret in distributed client software. The admin page permits inline script only because it is a local utility interface; a production UI needs a nonce/hash CSP and hardened identity provider integration.

## What would change for a real production deployment
Use OIDC with JWKS/key rotation, store hashed SDK keys with secure comparison and rotation metadata, encrypt secrets/data at rest, require TLS/HSTS at the edge, use a durable outbox and distributed pub/sub for SSE invalidation, add database migrations/backups, attach ticket/change-management evidence, redact logs centrally, add WAF/abuse monitoring, and run independent security testing.

## Explicit non-claims
This is a self-directed engineering demonstration. No formal security audit, penetration test, or compliance certification (PCI DSS, SOC 2, HIPAA, ISO 27001) has been performed or is claimed.
