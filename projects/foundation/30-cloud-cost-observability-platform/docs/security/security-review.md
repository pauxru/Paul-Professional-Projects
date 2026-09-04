# Security Review — Northstar Cloud Cost & Observability Platform

## Scope and method
This review examined the local .NET API, SQLite persistence, development token issuer, import path, dashboard, data flow, and deployment configuration against STRIDE. It is a design/code review of this self-directed reference implementation, not a live environment assessment.

## Assets
- Cost records, discounts, commitment coverage, forecasts, and allocation audit trails: competitive financial intelligence.
- Resource inventory and tags: potentially reveal architecture, team ownership, and workload criticality.
- JWT signing configuration and access tokens.
- Recommendation state and realised-savings evidence.

## Trust boundaries
1. Browser/API client to API: bearer tokens, request validation, rate limits, and headers apply.
2. API to local SQLite: EF Core parameterisation and scoped service access apply.
3. Provider-shaped export file to adapter: schema mapping and resource existence checks apply.
4. Development configuration to JWT issuer: the default key is intentionally local-only and startup rejects it in Production.

## Data classification
All included rows are fictional/synthetic and PII-free. In a real deployment, raw cost and inventory data should be classified as confidential competitive intelligence; it can disclose supplier rates, internal applications, geography, and engineering ownership. Tags are not assumed harmless merely because they are metadata.

## Threat model (STRIDE per boundary)
| Boundary | S | T | R | I | D | E |
|---|---|---|---|---|---|---|
| Client → API | Forged token | malformed actions | denied state-change history | team data over-read | request flood | read token used to mutate |
| API → SQLite | injected caller identity | altered rows/rules | missing action evidence | broad query exposes allocation | expensive aggregation | direct DB access |
| Export → adapter | untrusted file identity | restated/duplicate rows | disputed import | path points to wrong export | oversized/bad CSV | provider field misuse |
| Config → auth | default key abuse | changed issuer/audience | token issue ambiguity | secret leakage | token issuance flood | development endpoint in production |

## Mitigations implemented
- JWT bearer validation uses issuer, audience, signing key, expiry, and policy-based `finops:read`, `finops:manage`, and `finops:admin` scopes.
- A non-admin `team` claim overrides requested team query values. Integration tests prove a commerce caller cannot read data through `?team=data`.
- The development token endpoint returns 404 in Production, and a Production startup guard rejects the placeholder key.
- Resource/cost API results are JSON, `ProblemDetails` carries validation errors/trace ID, page size is capped, and import accepts CSV paths only.
- EF Core is used for persistence; unique billing identity indexes make replays/restatements safe.
- State-changing budget, allocation-rule, recommendation, and anomaly actions write append-only audit records with an SHA-256 before/after hash and correlation ID.
- Middleware emits HSTS, nosniff, frame denial, no-referrer, CSP, and Permissions-Policy headers. CORS uses an explicit configured origin.
- Global fixed-window rate limiting returns 429 with `Retry-After`.
- Synthetic data contains no real person, customer, subscription, credential, or operational data.

## Residual risk
The local file-path import endpoint trusts the host user; a real system must use authenticated upload/object storage with size, malware, and content validation. HS256 development tokens are intentionally unsuitable for enterprise production. Tag values can become an authorization pitfall if an external caller is allowed to choose a tag filter; this implementation treats `team` as a server-verified claim scope instead. SQLite file permissions are host dependent.

## What would change for a real production deployment
Use Entra ID/OIDC JWKS validation, managed identities, Key Vault, encrypted database/object storage, a warehouse/read model, network isolation, export allow-lists, signed provider delivery, AV scanning, retention/deletion controls, approval workflows, alert routing, and independent audit-log retention. Restrict cost export/download by policy and record every export recipient and purpose.

## Explicit non-claims
This is a self-directed engineering demonstration. No formal security audit, penetration test, or compliance certification (PCI DSS, SOC 2, HIPAA, ISO 27001) has been performed or is claimed.
