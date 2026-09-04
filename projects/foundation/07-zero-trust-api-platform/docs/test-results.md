# Test Results — Project 07 Zero-Trust API Platform

**Environment**: Windows 11 · .NET SDK 10.0.400 · target `net10.0` · SQLite in-memory-ish file DB (isolated per test factory)
**Command sequence**:

```powershell
dotnet build -c Release
dotnet test  -c Release --no-build --logger "console;verbosity=normal"
```

**No external infrastructure** was started. No Docker. No Postgres. No Redis. No Python.
The tests boot the API in-process via `WebApplicationFactory<Program>` and use SQLite files under a per-test workspace.

---

## Build

```
Determining projects to restore...
  All projects are up-to-date for restore.
  ZeroTrust.Domain            -> ...\src\ZeroTrust.Domain\bin\Release\net10.0\ZeroTrust.Domain.dll
  ZeroTrust.Application       -> ...\src\ZeroTrust.Application\bin\Release\net10.0\ZeroTrust.Application.dll
  ZeroTrust.Infrastructure    -> ...\src\ZeroTrust.Infrastructure\bin\Release\net10.0\ZeroTrust.Infrastructure.dll
  ZeroTrust.UnitTests         -> ...\tests\ZeroTrust.UnitTests\bin\Release\net10.0\ZeroTrust.UnitTests.dll
  ZeroTrust.Api               -> ...\src\ZeroTrust.Api\bin\Release\net10.0\ZeroTrust.Api.dll
  ZeroTrust.IntegrationTests  -> ...\tests\ZeroTrust.IntegrationTests\bin\Release\net10.0\ZeroTrust.IntegrationTests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:05.30
```

Verified with `dotnet build -c Release --no-incremental` (clean rebuild, no cached artefacts).

An earlier coordinator re-run flagged a single `EF1002` warning on the integration-test raw-SQL
restore in `ApiKeyMigrationTests.Api_Key_Rejected_After_Cutover_When_Past_Deprecation`. The
warning was **fixed properly** (not suppressed) by switching from
`ExecuteSqlRawAsync($"…{restoreIso}…")` to
`ExecuteSqlAsync($"UPDATE api_keys SET DeprecatedAfterUtc = {restoreValue} WHERE KeyId = 'acme-legacy-key-01'")`.
The `ExecuteSqlAsync` overload takes a `FormattableString` and binds each interpolated value
as a parameter, so the resulting SQL is `... = @p0 ...`. The clean rebuild above is warning-free.

---

## Test Summary

| Assembly | Total | Passed | Failed | Skipped | Duration |
|----------|------:|-------:|-------:|--------:|---------:|
| ZeroTrust.UnitTests         | 27 | 27 | 0 | 0 | 3.57s |
| ZeroTrust.IntegrationTests  | 38 | 38 | 0 | 0 | 5.46s |
| **Grand total**             | **65** | **65** | **0** | **0** | ~9s |

The threat model called for **≥ 30** tests; the delivered suite has **65**.

---

## Unit tests (27 / 27 passed)

```
Passed ZeroTrust.UnitTests.Domain.ApiKeyLifecycleTests.Usable_During_Dual_Accept_Even_If_Past_Deprecation_When_Enforcement_Off [5 ms]
Passed ZeroTrust.UnitTests.Domain.BreakGlassGrantTests.Active_While_In_Window_And_Not_Used [5 ms]
Passed ZeroTrust.UnitTests.Domain.RefreshTokenTests.Expired_Token_Is_Inactive [5 ms]
Passed ZeroTrust.UnitTests.Domain.AccountOwnershipTests.IsOwnedBy_Returns_True_For_Owner [6 ms]
Passed ZeroTrust.UnitTests.Domain.BreakGlassGrantTests.Inactive_After_MarkUsed [< 1 ms]
Passed ZeroTrust.UnitTests.Domain.RefreshTokenTests.Fresh_Token_Is_Active [< 1 ms]
Passed ZeroTrust.UnitTests.Domain.ApiKeyLifecycleTests.Usable_When_Not_Deprecated [< 1 ms]
Passed ZeroTrust.UnitTests.Domain.AccountOwnershipTests.IsOwnedBy_Returns_False_For_Others [< 1 ms]
Passed ZeroTrust.UnitTests.Domain.ApiKeyLifecycleTests.Unusable_Past_Deprecation_When_Enforcement_Active [< 1 ms]
Passed ZeroTrust.UnitTests.Domain.AccountOwnershipTests.IsOwnedBy_Returns_False_For_Empty_Subject [< 1 ms]
Passed ZeroTrust.UnitTests.Domain.RefreshTokenTests.Consumed_Token_Is_Inactive [< 1 ms]
Passed ZeroTrust.UnitTests.Domain.ApiKeyLifecycleTests.Unusable_After_Revoke [< 1 ms]
Passed ZeroTrust.UnitTests.Domain.RefreshTokenTests.Revoked_Token_Is_Inactive [< 1 ms]
Passed ZeroTrust.UnitTests.Domain.BreakGlassGrantTests.Constructor_Requires_Two_Person_Rule [2 ms]
Passed ZeroTrust.UnitTests.Domain.BreakGlassGrantTests.Constructor_Requires_Justification_Length [< 1 ms]
Passed ZeroTrust.UnitTests.Domain.BreakGlassGrantTests.Inactive_After_Expiry [< 1 ms]
Passed ZeroTrust.UnitTests.Security.HmacWebhookTests.Bad_Signature_Format_Rejected [16 ms]
Passed ZeroTrust.UnitTests.Security.HmacWebhookTests.Signature_With_Wrong_Version_Rejected [1 ms]
Passed ZeroTrust.UnitTests.Security.HmacWebhookTests.Valid_Signature_Passes [2 ms]
Passed ZeroTrust.UnitTests.Security.HmacWebhookTests.Expired_Signature_Rejected [< 1 ms]
Passed ZeroTrust.UnitTests.Security.HmacWebhookTests.Tampered_Body_Rejected [< 1 ms]
Passed ZeroTrust.UnitTests.Security.HmacWebhookTests.Replayed_Nonce_Rejected [< 1 ms]
Passed ZeroTrust.UnitTests.Security.SecretHasherTests.Same_Password_Different_Salt_Yields_Different_Hash [71 ms]
Passed ZeroTrust.UnitTests.Security.SecretHasherTests.Hash_Then_Verify_Succeeds [58 ms]
Passed ZeroTrust.UnitTests.Security.SecretHasherTests.Verify_Rejects_Wrong_Password [78 ms]
Passed ZeroTrust.UnitTests.Security.AuditHashChainTests.Chain_Detects_Tampering_When_Detail_Modified [820 ms]
Passed ZeroTrust.UnitTests.Security.AuditHashChainTests.Chain_Verifies_When_Untampered [5 ms]

Test Run Successful.
Total tests: 27
     Passed: 27
 Total time: 3.5676 Seconds
```

---

## Integration tests (38 / 38 passed)

```
Passed ZeroTrust.IntegrationTests.Endpoints.AuthAndJwksTests.Wrong_Password_Is_401_Unauthorized [271 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.CustomerSurfaceTests.Alg_None_Tampered_Token_Rejected [271 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.ApiKeyMigrationTests.Invalid_Api_Key_Secret_Yields_401 [266 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.AuthAndJwksTests.OpenId_Configuration_Advertises_Endpoints [41 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.AuthAndJwksTests.Client_Credentials_Narrows_To_Allowed_Scopes [71 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.PartnerSurfaceTests.Service_Token_Cannot_Be_Used_On_Partner_Api [466 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.CustomerSurfaceTests.Missing_Scope_Yields_Forbidden [179 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.ApiKeyMigrationTests.Valid_Api_Key_Grants_Partner_Access_During_Dual_Accept [188 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.PartnerSurfaceTests.Initiate_Payment_Requires_Auth [54 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.CustomerSurfaceTests.No_Token_Returns_401 [91 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.AuthAndJwksTests.Password_Grant_Issues_Access_And_Refresh [116 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.RateLimitTests.Partner_Rate_Limit_Triggers_429_With_Retry_After [561 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.AdminSurfaceTests.Admin_With_Step_Up_Can_Read_Audit [579 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.ApiKeyMigrationTests.Api_Key_Rejected_After_Cutover_When_Past_Deprecation [108 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.CustomerSurfaceTests.Security_Headers_Present [47 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.PartnerSurfaceTests.Inbound_Webhook_Signature_Missing_Returns_400 [52 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.AuthAndJwksTests.Client_Credentials_Grant_Issues_Partner_Token [69 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.KeyRotationTests.Token_Issued_Before_Rotation_Still_Valid_After_Rotation [675 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.PartnerSurfaceTests.Service_Token_Cannot_Be_Used_On_Customer_Api [53 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.CustomerSurfaceTests.Wrong_Audience_Is_Rejected [67 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.AdminSurfaceTests.Break_Glass_Flow_Records_Grant_And_Use_In_Audit [137 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.KeyRotationTests.Kid_In_JWKS_Includes_Primary_Signing_Key [51 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.PartnerSurfaceTests.Missing_Initiate_Scope_Yields_Forbidden [47 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.CustomerSurfaceTests.Alice_Sees_Only_Her_Own_Accounts [59 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.AuthAndJwksTests.Refresh_Rotation_Detects_Reuse_And_Revokes_Family [110 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.AdminSurfaceTests.Break_Glass_Requires_Approver_Different_From_Requestor [38 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.AuthAndJwksTests.Jwks_Endpoint_Returns_Keys [6 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.CustomerSurfaceTests.Correlation_Id_Echoed [58 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.PartnerSurfaceTests.Initiate_Payment_Returns_Created_And_Idempotent_On_Replay [108 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.CustomerSurfaceTests.Alice_Can_Read_Her_Own_Account_By_Id [60 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.PartnerSurfaceTests.Initiate_Payment_Requires_Client_Cert_Thumbprint_Header [36 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.AdminSurfaceTests.Key_Rotation_Adds_A_New_Key [181 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.CustomerSurfaceTests.Alice_Cannot_Read_Bobs_Account_By_Guessing_Id [69 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.AdminSurfaceTests.Admin_Endpoint_Requires_Step_Up [70 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.AdminSurfaceTests.Authz_Explain_Allows_Correct_Access [59 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.AdminSurfaceTests.Authz_Explain_Returns_Deciding_Requirement_For_Ownership [64 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.AdminSurfaceTests.Api_Key_Migration_Report_Includes_Legacy_Key [43 ms]
Passed ZeroTrust.IntegrationTests.Endpoints.AdminSurfaceTests.Audit_Verify_Chain_Endpoint_Reports_Ok [36 ms]

Test Run Successful.
Total tests: 38
     Passed: 38
 Total time: 5.4556 Seconds
```

---

## Requirement → test coverage

| Spec requirement | Covering test(s) |
|---|---|
| JWKS validation + key rotation | `KeyRotationTests.Kid_In_JWKS_Includes_Primary_Signing_Key`, `KeyRotationTests.Token_Issued_Before_Rotation_Still_Valid_After_Rotation`, `AdminSurfaceTests.Key_Rotation_Adds_A_New_Key` |
| `alg=none` / tampered token rejected | `CustomerSurfaceTests.Alg_None_Tampered_Token_Rejected` |
| Wrong audience rejected | `CustomerSurfaceTests.Wrong_Audience_Is_Rejected` |
| Scope enforcement per surface | `CustomerSurfaceTests.Missing_Scope_Yields_Forbidden`, `PartnerSurfaceTests.Missing_Initiate_Scope_Yields_Forbidden`, `AuthAndJwksTests.Client_Credentials_Narrows_To_Allowed_Scopes` |
| Ownership (IDOR) denies another user's resource | `CustomerSurfaceTests.Alice_Cannot_Read_Bobs_Account_By_Guessing_Id`, `CustomerSurfaceTests.Alice_Sees_Only_Her_Own_Accounts`, `CustomerSurfaceTests.Alice_Can_Read_Her_Own_Account_By_Id` |
| Refresh rotation + reuse detection revokes family | `AuthAndJwksTests.Refresh_Rotation_Detects_Reuse_And_Revokes_Family` |
| API key valid/expired/revoked/post-cutover | `ApiKeyMigrationTests.Valid_Api_Key_Grants_Partner_Access_During_Dual_Accept`, `ApiKeyMigrationTests.Invalid_Api_Key_Secret_Yields_401`, `ApiKeyMigrationTests.Api_Key_Rejected_After_Cutover_When_Past_Deprecation`, unit `ApiKeyLifecycleTests.*` |
| Dual-accept phase | `ApiKeyMigrationTests.Valid_Api_Key_Grants_Partner_Access_During_Dual_Accept` |
| Migration report | `AdminSurfaceTests.Api_Key_Migration_Report_Includes_Legacy_Key` |
| Rate limit 429 + Retry-After | `RateLimitTests.Partner_Rate_Limit_Triggers_429_With_Retry_After` |
| Webhook signature valid / invalid / expired / replay | `HmacWebhookTests.Valid_Signature_Passes`, `.Bad_Signature_Format_Rejected`, `.Expired_Signature_Rejected`, `.Replayed_Nonce_Rejected`, `.Tampered_Body_Rejected`, `.Signature_With_Wrong_Version_Rejected`, `PartnerSurfaceTests.Inbound_Webhook_Signature_Missing_Returns_400` |
| Outbound signing round-trip | `HmacWebhookTests.Valid_Signature_Passes` (issuer/verifier symmetry) |
| Service token rejected on customer & partner API | `PartnerSurfaceTests.Service_Token_Cannot_Be_Used_On_Customer_Api`, `.Service_Token_Cannot_Be_Used_On_Partner_Api` |
| Step-up required for admin | `AdminSurfaceTests.Admin_Endpoint_Requires_Step_Up`, `.Admin_With_Step_Up_Can_Read_Audit` |
| Break-glass requires approval and is audited | `AdminSurfaceTests.Break_Glass_Flow_Records_Grant_And_Use_In_Audit`, `.Break_Glass_Requires_Approver_Different_From_Requestor`, unit `BreakGlassGrantTests.*` |
| Audit hash chain detects tampering | `AuditHashChainTests.Chain_Detects_Tampering_When_Detail_Modified`, `.Chain_Verifies_When_Untampered`, `AdminSurfaceTests.Audit_Verify_Chain_Endpoint_Reports_Ok` |
| Authz-explain returns deciding requirement | `AdminSurfaceTests.Authz_Explain_Returns_Deciding_Requirement_For_Ownership`, `.Authz_Explain_Allows_Correct_Access` |
| ProblemDetails shape | `CustomerSurfaceTests.Security_Headers_Present` + `.No_Token_Returns_401` (asserts ProblemDetails 401 body) |
| 401 vs 403 correctness | `CustomerSurfaceTests.No_Token_Returns_401` (401), `CustomerSurfaceTests.Missing_Scope_Yields_Forbidden` (403), `PartnerSurfaceTests.Missing_Initiate_Scope_Yields_Forbidden` (403), `AuthAndJwksTests.Wrong_Password_Is_401_Unauthorized` (401) |
| OIDC-ish discovery advertises endpoints | `AuthAndJwksTests.OpenId_Configuration_Advertises_Endpoints` |
| JWKS keys endpoint | `AuthAndJwksTests.Jwks_Endpoint_Returns_Keys` |
| Password grant / client credentials grant | `AuthAndJwksTests.Password_Grant_Issues_Access_And_Refresh`, `.Client_Credentials_Grant_Issues_Partner_Token` |
| Correlation id echoed | `CustomerSurfaceTests.Correlation_Id_Echoed` |
| Partner idempotency on payment initiation | `PartnerSurfaceTests.Initiate_Payment_Returns_Created_And_Idempotent_On_Replay` |
| Partner requires `X-Client-Cert-Thumbprint` (simulated mTLS) | `PartnerSurfaceTests.Initiate_Payment_Requires_Client_Cert_Thumbprint_Header` |

Every spec requirement is covered by at least one test.

---

## Notes on reliability

- The rate-limit test runs against a **dedicated `LowLimitFactory`** (permits=6) so it does not
  share partition state with the main integration test factory which sets very high limits to
  keep the other 30+ tests fast and stable.
- SQLite files are created per test factory, in the OS temp folder, and deleted on dispose;
  tests do not leak state.
- The full run (build + test) completes in under 30 seconds on a warm machine.

---

## Live demo — `scripts\demo.ps1` end-to-end

The demo was executed against a live API for the first time in the follow-up verification pass.

**Sequence run**:

```powershell
# terminal A — API
$env:ASPNETCORE_URLS = "http://localhost:5007"
dotnet run --project src\ZeroTrust.Api --no-build -c Release --launch-profile http
#  Now listening on: http://localhost:5007

# terminal B — demo
pwsh -NoProfile -ExecutionPolicy Bypass -File scripts\demo.ps1
```

**Raw transcript** (captured from Tee-Object):

```
* 1. Trust core
  [ok] JWKS returns 1 key(s); primary kid = m--4xbxl_heIJXQC
  [ok] Discovery issuer = https://zero-trust-demo.localhost
* 2. Customer surface
  [ok] Alice obtained bearer + refresh
  [ok] Alice sees 2 account(s), all owned by 'alice'
    Sample: NTSF-0002-ALICE balance=1250000 USD
  [ok] IDOR blocked: Alice cannot read Bob's account (403)
* 3. Partner surface
  [ok] Partner ACME-TREASURY obtained access token
  [ok] Payment initiated: 2cc508b2-4ea4-4f45-acf6-7ba72c633122 status=Accepted
  [ok] Idempotency: replay returned the same payment id
* 4. Admin surface
  [ok] Admin obtained step-up token
  [ok] Audit contains 5 recent record(s) of 15 total
  [ok] Audit hash chain verifies clean
  [ok] PDP: allowed=True deciding=policy.satisfied reason=ok
  [ok] Migration report: 1 key(s) tracked
  [ok] Rotation performed: JWKS now advertises 2 key(s)
  [ok] JWKS now shows 2 key(s) - old primary still verifiable for its window
* Demo complete.
```

Every checkpoint the demo verifies passes. What each `[ok]` line proves:

| # | Line | Verifies |
|---|---|---|
| 1 | JWKS returns 1 key | The API is really issuing keys from the RSA key set; primary `kid` is `m--4xbxl_heIJXQC` for this process. |
| 2 | Discovery issuer | OIDC-ish `.well-known/openid-configuration` is served and advertises the correct issuer. |
| 3 | Alice obtained bearer + refresh | Password grant issues a real RS256 JWT with an accompanying refresh token. |
| 4 | Alice sees 2 account(s) | Ownership-scoped list — Alice sees exactly her two seeded accounts (`NTSF-0001-ALICE`, `NTSF-0002-ALICE`), not Bob's. |
| 5 | IDOR blocked (403) | The ownership `IAuthorizationHandler` denies Alice access to Bob's account by GUID even with a valid token. |
| 6 | Partner ACME-TREASURY obtained access token | Client-credentials grant returns a partner-audience JWT with narrowed scopes. |
| 7 | Payment initiated | End-to-end partner request succeeds: token + scope + simulated-mTLS thumbprint + idempotency key + valid payload => `201 Accepted` with a resource id. |
| 8 | Idempotency replay | Second POST with the same `Idempotency-Key` returns the same payment id, not a new one. |
| 9 | Admin obtained step-up token | Password grant with `amr=mfa` and `acr=urn:ntsf:acr:step-up` claims. |
| 10 | Audit contains 5 recent of 15 total | The audit log is real: 15 records were appended during this run (auth issuance, payment initiation, admin reads, etc.) and the page-size query is honoured. |
| 11 | Audit hash chain verifies clean | The tamper-evident hash chain re-computes correctly for every appended record. |
| 12 | PDP allowed=True deciding=`policy.satisfied` reason=`ok` | The explainable-authz endpoint runs Alice's token through the actual policy evaluation and returns the deciding requirement, not a boolean. |
| 13 | Migration report: 1 key tracked | The API-key migration reporting really queries the DB and returns the seeded legacy key. |
| 14 | Rotation JWKS advertises 2 keys | Admin key rotation produces a new signing key while the old one stays discoverable. |
| 15 | JWKS now shows 2 keys | The public JWKS document reflects the rotation immediately. |

**Fixes applied during the live-run pass**:

1. `demo.ps1` had `Authorization = "******"` in every header (redaction from a prior edit that
   never got cleaned up). Replaced with a `BearerHeaders` helper that builds
   `{ Authorization = "Bearer <token>" }` correctly.
2. `demo.ps1` used JSON property names `debtorAccount` and `creditorAccount`, but the API DTO
   is `DebtorAccountNumber` / `CreditorAccountNumber`. Fixed the JSON payload.
3. `demo.ps1` contained em-dash characters (`—`) that Windows PowerShell 5.1 misparses when
   reading UTF-8-no-BOM as CP1252. Replaced with ASCII hyphens so the script runs on both
   `powershell` (5.x) and `pwsh` (7.x). The transcript above was captured with `pwsh 7.6.5`.

After these three fixes the demo runs green start-to-finish; the process was then stopped and
port 5007 confirmed free.
