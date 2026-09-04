# Security Review — Northstar Insurance Claims Modernization Lab

## Scope and method
This review compares the deliberately unsafe legacy simulation with the modern target implementation. It is a source/design review of the local demonstration: authentication, authorization, input, persistence, document storage, error handling, and operational controls were inspected alongside automated exploit/authorization tests.

## Assets
- Claim references, status, financial amounts, reserves, and settlements.
- Policyholder names/emails and policy terms.
- Claim document content and storage keys.
- JWT signing material and authorization scope claims.
- Migration reports, source backup, and reconciliation evidence.

## Trust boundaries
1. Browser/client to legacy MVC host.
2. Client to modern HTTP API.
3. Modern API to JWT validation/token issuer.
4. Application layer to SQLite/Npgsql persistence.
5. Application layer to local or future Azure Blob document storage.
6. Legacy SQLite schema to anti-corruption importer.

## Data classification
All committed seed data is fictional. In a real deployment, claims, policy terms, contact data, documents, and authentication tokens would be confidential; migration reports may contain sensitive operational metadata. Logs should record identifiers/correlation IDs, not document content or full financial payloads.

## Threat model (STRIDE per boundary)
| Boundary | S | T | R | I | D | E | Modern control / legacy comparison |
|---|---|---|---|---|---|---|---|
| Client → legacy MVC | No strong identity boundary | Concatenated filter permits SQL tampering | Console/Trace only | Broad query disclosure | Blocking calls degrade service | Session workflow weakly scoped | Intentionally unsafe demonstration; do not expose publicly |
| Client → modern API | JWT signature/issuer/audience | DTO/domain validation; EF parameters | Correlation ID and structured logs | Scope policies, errors omit stack details | fixed-window rate limit | separate read/adjust/approve policies | Stronger boundary, local dev token endpoint only outside Production |
| API → database | Service identity is local process | EF constraints, unique indexes, concurrency token | database/history plus logs | parameterized EF query | bounded pagination | app-only schema access | SQLite local file still requires host filesystem controls |
| API → documents | N/A in local adapter | safe generated key, type/size rules | log/correlation available | path filename sanitization, outside web root | max byte cap | adapter boundary | No malware scanning yet |
| Legacy → importer | Operator-controlled source | ACL validates/map/rejects | report counts/checksums/rejections | no direct legacy schema in domain | offline controlled job | importer has approve policy endpoint | Requires protected operator execution |

## Mitigations implemented
| Area | Legacy posture | Modern posture |
|---|---|---|
| SQL | `DatabaseHelper.cs:192` concatenates user input; exploit test proves filter bypass | EF LINQ query is parameterized; matching injection test returns no results |
| Configuration | Static XML values, no validation | Typed `DatabaseOptions`, `JwtOptions`, document options with startup validation |
| Authentication | No claims API identity boundary | JWT bearer issuer/audience/signature/lifetime validation |
| Authorization | Controller workflows have no policy enforcement | `claims:read`, `claims:adjust`, and `claims:approve` endpoint policies, with 401/403 tests |
| Workflow integrity | Free-form stage path in controller | Explicit aggregate state transition matrix |
| Concurrency | Last writer wins | EF `Version` concurrency token and 409 response |
| Auditability | Console/Trace records lack actor/resource integrity | Append-only `AuditRecords` capture actor, action, resource, timestamp, correlation/source metadata, and hashes of before/after representations |
| Input/errors | Broad catch/swallow and generic MVC behavior | DTO data annotation validation, domain revalidation, RFC 7807 response handler |
| Documents | Direct `File.Create`, original filename, swallowed exception | `IDocumentStore`, generated key, `Path.GetFileName`, PDF/JPEG/PNG allow-list, byte cap, async I/O |
| HTTP hardening | No stated headers/abuse limits | correlation, nosniff, DENY frame, referrer, CSP, permissions headers; rate limiting |
| Observability | `Console.WriteLine` / `Trace.Write` | `ILogger`, correlation scope, OpenTelemetry ASP.NET Core tracing, health endpoints |

The repository contains no real secrets or personal data. Development signing material is visibly named `dev-only-not-a-real-secret...`, excluded local overrides are in `.gitignore`, and startup refuses that default key in Production.

## Residual risk
- The local HS256 developer token endpoint is intentionally only for development/testing; it is not a production identity design.
- SQLite file permissions, host backups, and encryption-at-rest are outside application code.
- No anti-malware/content scanning, quarantine workflow, retention policy, or object-store encryption configuration is implemented.
- Audit record retention, tamper-evident external storage, and audit-alert rules are not yet implemented beyond the append-only application table.
- Rate limiting is process-local and not distributed.
- The legacy simulation remains unsafe by design and must not be internet exposed.

## What would change for a real production deployment
Use an external OIDC provider with JWKS/key rotation, a managed secret store, managed database with least-privilege identities and tested provider migrations, Azure Blob (or equivalent) with private access/encryption/versioning, malware scanning before document availability, centralized structured logs and SIEM retention, externally immutable audit retention, distributed rate limiting, threat-model review of the routing facade, penetration testing, and privacy/retention assessment.

## Explicit non-claims
This is a self-directed engineering demonstration. No formal security audit, penetration test, or compliance certification (PCI DSS, SOC 2, HIPAA, ISO 27001) has been performed or is claimed.
