# Security Review — Rift Valley Credit Loan Origination Platform

## Scope and method
This lightweight design review examined the .NET API, SQLite snapshots, local document adapter, deterministic simulators, JWT scopes, audit chain, and state-changing workflow endpoints using STRIDE. It is focused on synthetic demonstration data and code-level controls, not a formal assessment.

## Assets
- Synthetic customer identity/contact/profile data and uploaded document bytes.
- Immutable product/ruleset versions, applicant facts, decision traces, score contributions, and decision records.
- JWT signing configuration and scope claims.
- Offer acceptance, delegated approval, disbursement references/statuses, and audit entries.

## Trust boundaries
1. Client to API: bearer token, request validation, rate limiting, and correlation propagation.
2. API/Application to SQLite: EF Core repository and optimistic version check.
3. Application to local filesystem: object-store interface, generated keys, file-type/size allow-list, and path traversal rejection.
4. Application to KYC/bureau/disbursement ports: deterministic local adapter now; authenticated/signed vendor integrations in a real deployment.
5. Privileged credit operations actions: scope policy plus delegated-authority and four-eyes domain checks.

## Data classification
All seeded applicant information is synthetic and marked fictional. In a real lender, identity, contact data, bureau returns, documents, and decision history are highly sensitive personal/financial data; decision traces can also reveal policy logic and must receive controlled access. This repository contains no real personal data, credentials, or provider keys.

## Threat model (STRIDE per boundary)

| Boundary | S — spoofing | T — tampering | R — repudiation | I — disclosure | D — denial of service | E — elevation |
|---|---|---|---|---|---|---|
| Client → API | JWT issuer/audience/signature/lifetime validation | DTO/domain validation; optimistic application version | append-only audit + correlation ID | scoped endpoints; no secrets returned | fixed-window rate limiting | policy scopes |
| API → SQLite | service boundary | unique indexes, version checks, immutable version keys | audit hash chain | local default only; no SQL interpolation | indexed/bounded pagination | repository is not exposed directly |
| Application → documents | generated storage keys | content type/size checks; traversal prevention | upload/review audit events | filesystem outside web root | size cap | review endpoint requires underwriting scope |
| Application → provider ports | deterministic local identity | idempotency reference/status validation | request/callback audits | no real provider payloads | timeout retry + pending state | manual KYC override requires admin policy |
| Credit approval | named JWT subject | authority limit + distinct second approver | decision actor/reason/second approver stored | policies restrict queue/audit | bounded queue pages | four-eyes threshold and `loans:approve` |

## Mitigations implemented
- JWT bearer authentication with narrowly named scope policies and a Development/Testing-only local token endpoint.
- Startup rejects the known development signing key in Production; `.env` and local-secret patterns are ignored.
- RFC 7807 errors, request validation, fixed-window rate limiting, security headers, correlation IDs, and structured logging scope.
- `decimal` money calculations, immutable product/ruleset version binding, state-machine enforcement, and optimistic application versions.
- PDF/JPEG/PNG allow-list, per-requirement size cap, local storage outside web root, generated object keys, reviewer/reason/expiry metadata.
- Manual KYC override requires the admin policy at HTTP edge and an explicit elevated boolean in the application service; it is auditable.
- Append-only application audit API (read only) with before/after hashes and previous-entry chain.

## Residual risk
SQLite files and local documents are not encrypted at rest by this prototype. The audit chain is not externally anchored and an actor with filesystem/database access could alter all rows. The simulator callback uses an authenticated internal API policy rather than a provider HMAC signature; vendor webhooks and multi-instance concurrency are not modeled. Fuzzy duplicate matching can create false positives/negatives and needs human review workflow in a real system.

## What would change for a real production deployment
Use OIDC/JWKS and secret management; encrypted managed database/blob storage; signed HMAC webhook verification with timestamp, nonce replay protection, and constant-time comparison; malware scanning; private networking; managed identity; SIEM export; retention/legal-hold controls; least-privilege service identities; external audit anchoring; vendor due diligence; and privacy/fairness/model-risk governance. Add threat-driven penetration testing and operational security monitoring before accepting real applicant data.

## Explicit non-claims
This is a self-directed engineering demonstration. No formal security audit, penetration test, or compliance certification (PCI DSS, SOC 2, HIPAA, ISO 27001) has been performed or is claimed.
