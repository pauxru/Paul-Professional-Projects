# Security Review — Northstar Reliability Control Room

## Scope and method
This lightweight design review covers the local .NET API, SVG dashboard, JWT development flow, SQLite persistence, and expected automation clients. It uses STRIDE analysis at the browser/API, automation/API, API/database, and telemetry-ingestion boundaries. Source review and automated tests informed this document; it is not a formal audit.

## Assets
- Service ownership, on-call, runbook, repository, SLO, error-budget, and incident metadata.
- Synthetic telemetry and simulator scenarios.
- JWT signing configuration and bearer tokens.
- Deploy-gate decisions and postmortem action ownership.
- Local SQLite database integrity.

## Trust boundaries

| Boundary | Untrusted input | Primary controls |
|---|---|---|
| Browser or automation → API | bearer token, path/query/body | JWT validation, scope policies, edge validation, rate limiting |
| Simulator/metric client → API | aggregate counters and dimensions | authorization, required fields, domain counter invariants |
| API → SQLite | persisted aggregate/lifecycle state | EF Core parameterization, unique indexes, local filesystem permissions |
| Dashboard → API | API responses rendered in DOM | JSON-only API, text-content rendering for status text, CSP |

## Data classification
All seeded people, services, URLs, telemetry, and incidents are synthetic for Northstar Group (fictional). Operational metadata is treated as internal in a real deployment. No credentials, real personal data, customer traffic, or production incident data is included.

## Threat model (STRIDE per boundary)

| Boundary | S | T | R | I | D | E |
|---|---|---|---|---|---|---|
| Client → API | Signed JWT with issuer/audience/key validation | Input/domain validation and ProblemDetails | correlation ID/log scope | scoped policy and minimal error detail | fixed-window rate limit | separate read/write/admin policies |
| Metric client → API | `reliability.write` scope | non-negative counters; good values cannot exceed valid totals | correlation ID and timestamps | no raw request payload retention | bounded body model and rate limit | write scope cannot approve postmortems |
| API → SQLite | local adapter only | EF parameterization, unique keys, concurrency token on services | planned audit expansion; correlation ID in logs | local file access is host controlled | local database avoids network dependency | database is not exposed directly |
| Dashboard → API | token kept in browser session storage only for demo | CSP and text-oriented rendering | API correlation IDs | no secrets embedded in page | API rate limits | dashboard token requires explicit scopes |

## Mitigations implemented
- JWT bearer authentication, audience/issuer/signature/lifetime validation, and policy-based scopes.
- Development/testing-only local token helper; Production refuses the known development signing key.
- RFC 7807 validation/domain errors, non-negative telemetry invariants, and bounded page sizes.
- Security headers: HSTS, `nosniff`, DENY frame policy, no-referrer, CSP, and restrictive permissions policy.
- Fixed-window API rate limiting, correlation headers/log scope, and health endpoints.
- EF Core SQLite with indexes and no raw SQL.
- `.env`, databases, key files, and local settings excluded from git.

## Residual risk
The local development token endpoint and session-storage dashboard workflow are not appropriate for production. There is no user provisioning, token revocation, immutable audit table, encryption-at-rest management, secret store, webhook signature verification, or separate tenant isolation. A local SQLite file depends on host filesystem controls.

## What would change for a real production deployment
Use OIDC with JWKS rotation and workload identity; store secrets in a managed vault; apply a CORS allow-list and TLS termination; move metrics to a hardened time-series backend; use managed database backups/encryption; add append-only audit events; establish least-privilege service identities; use a secrets scanner/SAST/dependency monitoring; and conduct threat modeling, penetration testing, operational access review, and incident exercises.

## Explicit non-claims
This is a self-directed engineering demonstration. No formal security audit, penetration test, or compliance certification (PCI DSS, SOC 2, HIPAA, ISO 27001) has been performed or is claimed.
