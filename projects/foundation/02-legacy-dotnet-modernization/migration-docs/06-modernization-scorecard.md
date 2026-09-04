# Modernization Scorecard

Scores are comparative engineering assessments of this demonstration, not an external audit.

| Dimension | Legacy simulation | Modern target | Evidence |
|---|---|---|---|
| Testability | Low; static helpers/global config/controller coupling | High; pure domain service, ports, SQLite integration fixture | 7 legacy characterization, 29 domain, 8 shared, 11 integration tests |
| Cyclomatic-ish complexity | High; `ClaimsController.Create` combines validation, lookup, arithmetic, persistence, session, error/display paths (~8+ decision paths) | Lower per unit; endpoint validation and focused use-case methods (~2–5 paths) | `ClaimsController.cs` vs `ClaimApplicationService.cs` |
| Config safety | XML static reads/no validation | Typed options with data annotations and `ValidateOnStart` | `AppSettings.cs` vs API options/`Program.cs` |
| SQL/data safety | Raw ADO.NET; injected filter is exploitable | EF parameterized query, schema checks, indexes, unique keys | `DatabaseHelper.cs:115`, `EfClaimsStore.cs` |
| Workflow safety | String status accepts broad stages | Explicit allowed state graph | `Claim.IsAllowedTransition` |
| Concurrency | Last writer silently wins | Version token and 409 handling | `Claim.Version`, EF concurrency config, integration test |
| Documents | Direct file writes; swallowed failure | Port, allow-list, cap, async write | `IDocumentStore`, `LocalFileDocumentStore` |
| Security | No auth/policy boundary in relevant controller path | JWT bearer, scope policies, headers, rate limiter | API `Program.cs`, endpoint requirements |
| Auditability | Console/Trace has no reliable actor/resource evidence | Append-only `AuditRecords` capture state-change context and SHA-256 representations | `Audit.cs`, `EfAuditWriter.cs` |
| Observability | Console/Trace only | Correlation IDs, structured logs, OTel, health | API middleware |
| Deployability | Machine-local configuration/state | SQLite default plus Npgsql selection, documented Docker config | `.env.example`, options, Dockerfile |
| Migration readiness | No owned migration artifact | ACL, importer, report, cutover/runbook | `migration-docs/` |

## Remaining modernization backlog
The target deliberately does not claim production completeness. Highest-priority next steps are real OIDC/JWKS, managed secret/key rotation, malware scanning, append-only audit events, Azure Blob adapter, production database tests, routing facade implementation, and retry/dead-letter processing for external integrations.
