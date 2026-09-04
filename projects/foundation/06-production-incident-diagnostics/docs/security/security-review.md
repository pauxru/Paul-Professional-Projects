# Security Review — Production Incident Diagnostics Lab

## Scope and method
This review covers the Northstar Logistics (fictional) sample API, its local SQLite store, the development-token endpoint, diagnostics evidence written to disk, and the in-process scenario harness. The method is a lightweight architecture review using trust boundaries, data classification, and STRIDE prompts; it is not a penetration test.

## Assets
- Synthetic customer, order, and shipment records in SQLite.
- Development JWT signing-key configuration and issued bearer tokens.
- Incident evidence containing synthetic metrics, SQL text, and timing data.
- Availability of the local API and harness.
- Source integrity of the intentionally broken scenario paths.

## Trust boundaries
1. HTTP client → ASP.NET Core API.
2. API → JWT validation and local development issuer.
3. API → EF Core/SQLite database.
4. Harness → scenario implementations and evidence filesystem.
5. Sample API → simulated/real future outbound HTTP dependencies.

## Data classification
All committed demo data is fictional and deliberately non-personal. Runtime bearer tokens, local connection strings, and diagnostic captures are still sensitive operational data and must not be copied into public incident channels. `.env`, local databases, private keys, and local configuration overrides are ignored by Git.

## Threat model (STRIDE per boundary)
| Boundary | S | T | R | I | D | E | Key mitigations |
|---|---|---|---|---|---|---|---|
| Client → API | Forged token | Malformed JSON | Denied request trail | Error detail leakage | Request flood | Scope escalation | JWT validation, validation errors, correlation ID, fixed-window limiter, policy scopes |
| API → issuer | Dev issuer misuse | Signing-key replacement | Token attribution gap | Key disclosure | Token mint flood | Arbitrary scopes | Endpoint absent in Production, default-key startup guard, `.env` ignore |
| API → SQLite | Local process access | SQL injection | Unattributed write | DB file exposure | Expensive query | DB-level bypass | EF parameterization, constraints/indexes, scoped DbContext, local-only default |
| Harness → evidence | Path manipulation | Evidence modification | Unclear provenance | Metrics leak | Disk exhaustion | Unsafe scenario mode | Controlled incident folders, bounded workloads/deadlines, explicit labels and limitations |
| API → dependency | Spoofed endpoint | Response tamper | Missing call chain | Header/payload leakage | Slow/failing service | Retry amplification | Timeout/cancellation lab, retry budget, circuit breaker model, OpenTelemetry spans |

## Mitigations implemented
- Development-only HS256 JWT issuance is only mapped outside Production; issuer, audience, key, and lifetime are validated.
- `orders.read` and `orders.write` are policy-based scope checks; tests prove `401` and `403`.
- Request DTOs use data annotations plus domain invariants; EF Core uses parameterized query generation.
- Correlation ID middleware honours a bounded inbound ID and returns it in `X-Correlation-Id`.
- Security headers include HSTS, `nosniff`, `DENY` framing, no-referrer, CSP, and permissions policy.
- Fixed-window rate limiting protects API routes and returns `429`.
- SQLite option and JWT option objects are startup-validated. Production startup refuses the known development signing key.
- Scenarios use no real services or personal data; every potentially pathological loop has a hard budget and cleanup.

## Residual risk
The development issuer deliberately makes local API exploration easy and is not an identity provider. SQLite file permissions and evidence-folder access are host responsibilities. The API does not implement multi-tenancy, audit records, key rotation, secrets vault integration, CORS configuration, distributed rate limiting, or production incident artifact retention controls.

## What would change for a real production deployment
Use an OIDC provider with JWKS rotation and workload identity; put secrets in a managed secret store; use TLS termination and an explicit CORS allow-list; emit structured audit events; isolate tenants in persistence; use a server database with least-privilege identities; export telemetry to access-controlled storage; and validate timeout/retry/breaker policy against real dependencies. Add SAST, dependency scanning, secret scanning, threat-model review, and penetration testing to the delivery process.

## Explicit non-claims
This is a self-directed engineering demonstration. No formal security audit, penetration test, or compliance certification (PCI DSS, SOC 2, HIPAA, ISO 27001) has been performed or is claimed.
