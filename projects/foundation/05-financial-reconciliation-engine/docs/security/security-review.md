# Security Review

This review covers the .NET 10 Financial Reconciliation & Settlement Engine (`ReconEngine`) as a self-directed engineering case study using fictional financial data and fictional organisations. It is a design-and-implementation security review based on verified engineering facts and selected source inspection, not a penetration test, production certification, or claim of real-world operational assurance.

## Trust Boundaries

- **API boundary:** External callers cross into the system through the ASP.NET Core minimal API under `/api/v1`, with JWT bearer authentication, policy-based authorization for privileged routes, ProblemDetails error handling, correlation IDs, security headers, Serilog request logging, OpenTelemetry instrumentation, and a global fixed-window rate limiter.
- **File-upload boundary:** Authenticated callers submit `multipart/form-data` to `POST /api/v1/imports` with `file` and `profile`. The profile name is allow-listed by `BuiltInProfiles` (`internal-csv`, `external-csv`, `external-fixed`), and `ImportService.ImportAsync` streams tokenized rows into mapped `ReconRecord` entities while recording row-level rejections.
- **Database boundary:** Persistence crosses through Application store abstractions into EF Core-backed SQLite by default (`AppDbContext`). Domain values such as enums are persisted as strings for readable audit rows, and EF query/update paths provide parameterisation rather than ad-hoc SQL string concatenation.
- **Token issuance boundary:** `POST /api/v1/auth/token` is anonymous by design for development and integration testing. `TokenIssuer` signs HMAC-SHA256 JWTs with issuer, audience, `jti`, `sub`, lifetime, and `scope` claims; this developer issuer is **not** a production identity provider and must be replaced or disabled for production.

## STRIDE Analysis

| Threat | Category (Spoofing/Tampering/Repudiation/Information Disclosure/Denial of Service/Elevation of Privilege) | Vector in this system | Mitigation implemented | Residual risk |
|---|---|---|---|---|
| JWT spoofing | Spoofing | Caller presents a forged bearer token to `/api/v1` endpoints. | JWT bearer validation checks issuer, audience, HMAC signing key, and lifetime with 30-second clock skew. | Shared HMAC key strength depends on deployment configuration; default placeholder must be overridden. |
| Tampering with settlement files | Tampering | Uploaded internal/external CSV or fixed-width files are modified before ingestion. | `ImportService` records SHA-256 `FileChecksum`; row hashes support duplicate detection; import batches persist file name, profile, counts, and checksum. | Checksum proves what was ingested, not who authored the upstream file or whether it was malicious before upload. |
| Repudiation of write-offs | Repudiation | A user denies proposing, approving, rejecting, reopening, assigning, or commenting on an exception. | Four-eyes workflow requires different proposer/approver for high-value write-offs; every transition/comment appends immutable `ExceptionAuditEntry`; `Version` supports optimistic concurrency. | Audit actor identity depends on trusted JWT subject from the auth boundary; dev token issuer is not suitable for production attribution. |
| Information disclosure of financial data | Information Disclosure | Authenticated users read imports, runs, reports, exceptions, and CSV/JSON exports containing financial data. | API requires authentication for imports, rulesets, runs, exceptions, and reports; security headers include `nosniff`, `DENY` framing, no referrer, and restrictive CSP. | No per-tenant or row-level authorization is verified; protect transport, logs, database files, and backups in deployment. |
| DoS via huge file upload | Denial of Service | Caller uploads very large multipart bodies or many upload requests. | Global fixed-window rate limiter defaults to 100 requests per 10 seconds per IP; ingestion is streaming and does not load the whole file into memory. | No application-specific multipart byte limit is verified; configure Kestrel/reverse-proxy/request body limits for production. |
| Elevation via missing scope checks | Elevation of Privilege | Authenticated caller without the correct business permission invokes privileged write endpoints. | Policies `recon:run`, `recon:resolve`, and `recon:approve` require corresponding `scope` claims on run and exception workflow endpoints. | Some authenticated write endpoints, such as ruleset creation/activation and imports, are auth-only per verified facts; production may need additional scopes. |
| SQL injection | Tampering | User-controlled filters, IDs, paging, sort inputs, or request bodies influence database access. | EF Core store/query paths provide parameterisation; route parameters use typed GUIDs/enums where applicable. | Dynamic sorting/filtering should remain constrained to known fields; review any future raw SQL additions. |
| CSV injection on export | Information Disclosure | Exported report cells beginning with formula triggers execute when opened in a spreadsheet. | `CsvWriter.Encode` prefixes `'` for values starting with `=`, `+`, `-`, `@`, tab, or CR, then applies RFC-4180 quoting. | Downstream tools may transform CSV before spreadsheet use; keep export encoding centralized through `CsvWriter`. |
| Replay of tokens | Spoofing | A stolen bearer token is reused before expiry. | Tokens have finite lifetime (`Jwt.AccessTokenMinutes`, default 60) and include `jti`; lifetime validation is enabled. | No revocation list, refresh-token rotation, mTLS, or token binding is verified. |
| Correlation-id spoofing | Repudiation | Caller supplies `X-Correlation-ID` to influence logs or confuse incident analysis. | `CorrelationIdMiddleware` consistently echoes and logs one correlation ID per request, minting a GUID when absent. | Inbound values are reused and not cryptographically trusted; treat correlation IDs as diagnostics, not identity or proof. |
| Mass-assignment | Elevation of Privilege | Request bodies set fields that should be server-controlled, such as actor, status, timestamps, approval, or audit values. | Minimal API endpoints use purpose-specific request contracts (`StartRunRequest`, `AssignRequest`, `ResolveRequest`, `NoteRequest`, `CreateRuleSetRequest`) and server-side `ClaimsPrincipal.CurrentUser()` for actors. | Continue avoiding direct entity binding on public endpoints; ruleset definitions are intentionally user-supplied and should stay validated. |
| Enum/aging tampering | Tampering | Callers submit invalid enum values or manipulate report filters such as status, severity, type, currency, or aging format. | JSON serializes enums as strings; minimal API binds typed enum query/body values; aging export supports only JSON or `format=csv`; domain state machine rejects illegal exception transitions. | Currency strings and reporting filters should remain normalized and bounded; invalid model-binding behavior depends on ASP.NET Core defaults. |
| Cross-currency matching | Tampering | Records in different currencies are matched or added together to hide breaks. | Money addition throws across currencies; matching predicates include currency; reports and balance assertions are per currency. | Unknown currency decimal defaults exist in `CurrencyInfo`; production should enforce an allowed ISO currency set if required. |
| Rule-set tampering | Tampering | A caller changes matching logic after a run to alter audit interpretation. | `MatchingRuleSet` edits create new versions, `VersionTag` is recorded on matches and immutable runs, and activation is explicit. | Ruleset create/activate endpoints are authenticated but not scope-gated per verified facts. |
| Exception workflow race | Tampering | Concurrent users approve, reject, resolve, or reopen the same exception and overwrite state. | `ReconciliationException.Version` is bumped per transition for optimistic concurrency; illegal transitions throw. | Final conflict handling depends on store-level concurrency propagation and caller retry behavior. |

## Financial Data Handling

`Money` uses decimal input converted to minor units as `long`; reconciliation amounts and persisted `AmountMinor`, `FeeMinor`, and report totals avoid binary floating-point arithmetic. Currency is part of matching and reporting semantics: cross-currency `Money.Add` throws, matching includes currency, and balance assertions are calculated per currency rather than netting currencies together.

Runs are immutable snapshots with `RuleSetVersionTag`, `InputChecksum`, counts, totals JSON, exception breakdown JSON, and balance assertion status. The balance assertion self-check requires, per currency, `sum internal == sum matched-internal + sum unmatched-internal`; violations throw `BalanceAssertionException` and mark the run failed. Exception transitions and comments append immutable `ExceptionAuditEntry` rows, preserving the operational history around reconciliation breaks and resolutions.

## File-Upload Threats

- **Multipart size:** `POST /api/v1/imports` accepts multipart uploads, but no application-specific byte limit is verified in the reviewed facts/source. The implemented compensating controls are streaming ingestion and the global rate limiter; production should set explicit Kestrel or reverse-proxy body limits.
- **Streaming:** `ImportService.ImportAsync` reads through `HashingReadStream` and `IAsyncEnumerable` tokenizers so large files are not fully loaded into memory.
- **Row-level rejection:** Mapping failures are captured as `ImportRejection` with line number, reason, and truncated raw line; the batch records total, accepted, and rejected rows.
- **Checksum:** Each import batch stores a SHA-256 checksum of the raw uploaded bytes as `FileChecksum`.
- **Profile allow-list:** Only `BuiltInProfiles.ByName` profiles are accepted: `internal-csv`, `external-csv`, and `external-fixed`.
- **Formula/CSV injection on re-export:** Export uses `CsvWriter.Encode` to neutralise spreadsheet formula triggers before CSV quoting.
- **Decompression/zip:** The ingestion path accepts CSV and fixed-width plain text profiles only; there is no decompression or archive expansion path for zip files.
- **Encoding/BOM handling:** CSV tokenization is verified to handle BOM, quoted commas, embedded newlines, and blank lines. Fixed-width parsing remains profile-driven by start/length fields.

## CSV Injection on Export

`ReconEngine.Infrastructure.Export.CsvWriter.Encode` is an implemented control for report exports. It first prefixes a single quote (`'`) when a field begins with a spreadsheet formula trigger: `=`, `+`, `-`, `@`, tab, or carriage return. After neutralising that leading character, it applies RFC-4180 quoting when a field contains comma, double quote, LF, or CR, escaping embedded quotes by doubling them; `CsvWriter.Write` emits CRLF-delimited rows.

## RBAC & Authorization

Authorization is JWT bearer-based. `ReconScopes` defines `recon:run`, `recon:resolve`, and `recon:approve`, and `Program.cs` maps each value to an authorization policy requiring an authenticated user and the corresponding `scope` claim.

| Endpoint | Method | Required scope/policy | Notes |
|---|---:|---|---|
| `/api/v1/runs` | POST | `recon:run` | Starts a reconciliation run over the working set. |
| `/api/v1/exceptions/{id}/assign` | POST | `recon:resolve` | Assigns an exception. |
| `/api/v1/exceptions/{id}/comment` | POST | `recon:resolve` | Adds an exception comment and audit entry. |
| `/api/v1/exceptions/{id}/resolve` | POST | `recon:resolve` | Resolves or moves high-value write-offs to pending approval. |
| `/api/v1/exceptions/{id}/approve` | POST | `recon:approve` | Approves pending high-value write-offs; approver must differ from proposer. |
| `/api/v1/exceptions/{id}/reject` | POST | `recon:approve` | Rejects pending write-off approval. |
| `/api/v1/exceptions/{id}/reopen` | POST | `recon:resolve` | Reopens a resolved exception. |

Other verified API groups such as imports, rulesets, runs reads, exceptions reads, and reports require authentication, except `POST /api/v1/auth/token`, health checks, and OpenAPI. The development token endpoint defaults omitted scopes to all three scopes for demo/test convenience and is not a production access-control design.

## Four-Eyes / Segregation of Duties

The reconciliation exception state machine supports `Open -> Assigned -> (Resolved | PendingApproval -> Resolved)` and `Resolved -> Reopened`; illegal transitions throw `InvalidStateTransitionException`. A `Resolve(WriteOff, ...)` for an exception whose absolute amount is at least `WriteOffApprovalThresholdMinor` (`100000` minor units = `1000.00`) does not resolve immediately; it transitions to `PendingApproval`.

Approval and rejection must be performed by a different user than the proposer, and same-user approval is rejected as a four-eyes violation. Every assignment, comment, resolve, approval, rejection, and reopen operation appends an immutable `ExceptionAuditEntry`, while `ReconciliationException.Version` is bumped per transition to support optimistic concurrency.

## Secrets Management

Local secret files are excluded by `.gitignore`, including `.env`, `.env.local`, `appsettings.Development.local.json`, `secrets.json`, `*.pfx`, and `*.key`; the verified facts state that only `.env.example` is committed and no real secrets are in the repository. JWT settings are configurable under `Jwt`, with issuer, audience, key, and access-token lifetime; the appsettings key is a placeholder (`REPLACE_THIS_WITH_A_LONG_RANDOM_SECRET_AT_LEAST_32_CHARS`) intended to be overridden by configuration or environment.

For production, add a startup guard that fails closed when the placeholder/default JWT key is used outside development or testing. Also prefer an external identity provider and managed secret store over the development `TokenIssuer` and local configuration files.

## Explicit Non-Claims

- This is not a penetration test and does not claim exploit coverage.
- There is no verified external identity provider integration.
- The development token endpoint is anonymous by design for demo and integration-test use.
- TLS is assumed to be terminated by the host or reverse proxy; `UseHttpsRedirection` is intentionally omitted for the test host.
- There is no verified HSM/KMS-backed key management.
- This is not a production hardening certification or compliance attestation.
- Docker is unverified on the host; Dockerfile and docker-compose files were authored but not started.
