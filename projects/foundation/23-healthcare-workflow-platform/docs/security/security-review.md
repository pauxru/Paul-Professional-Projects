# Security Review

> ⚠️ **No compliance or certification is claimed.** This document describes *design-time
> security considerations* only. **No HIPAA, GDPR, or ISO 27001 assessment, audit, or
> certification has been performed and none is claimed.** The vocabulary of those frameworks
> is used only to make the design more precise; the code is a self-directed engineering case
> study, not a certified product.

## 1. Assets we care about

| Asset                        | Sensitivity | Notes                                                          |
| ---------------------------- | ----------- | -------------------------------------------------------------- |
| Patient demographics         | Personal    | Fictional in this project; would be real in a deployment.      |
| Clinical notes / vitals      | Special    | Higher sensitivity; access is guarded by RBAC + ABAC.          |
| Appointment schedule         | Personal    | Reveals treatment relationships and cadence.                   |
| Audit log                    | Integrity | Must never be edited; append-only enforced at persistence.     |
| Auth signing key             | Secret     | Environment variable, never checked in; sample only in .env.example. |

## 2. STRIDE analysis

| Threat                       | Attack surface / example                                      | Mitigation                                                       |
| ---------------------------- | ------------------------------------------------------------- | ---------------------------------------------------------------- |
| **S**poofing                 | Forged JWT; replay of stolen token.                           | HS256 signature verification; short token lifetime; auditor-visible principal on every audit row. |
| **T**ampering with data      | Direct SQL to modify clinical notes; audit log tampering.     | Notes and audit are append-only at the DbContext; DELETE/UPDATE throw. Filtered unique indexes prevent double-booking. |
| **R**epudiation              | Clinician denies reading a chart.                             | Every clinical read audited with actor / role / purpose / correlation id / break-glass flag. |
| **I**nformation disclosure   | Receptionist gaining sight of clinical notes; casual browsing by an unrelated clinician. | RBAC blocks receptionist from clinical endpoints; ABAC (care relationship) blocks unrelated clinicians. Break-glass records visible on anomaly report. |
| **D**enial of service        | Booking flood; slot-search flood.                             | ASP.NET Core rate limiter (fixed window, per-user); slot search is O(candidates) with indexed access. |
| **E**scalation of privilege  | Assigning yourself an admin role.                             | Dev-token endpoint is dev-only (must be disabled in production); role assignments live in a separate identity backend in real deployments. |

## 3. Control coverage

### Identity & authentication
- JWT bearer via ASP.NET Core `AddAuthentication().AddJwtBearer(...)`.
- HS256 signing key from configuration; never inlined.
- Dev-token endpoint mints a token for a fictional user and is *only* wired in `Development`.
- Correlation id per request: incoming `X-Correlation-Id` or a new GUID.

### Authorization
- Role policies enforce coarse access (`Receptionist`, `Nurse`, `Clinician`, `ClinicalLead`,
  `Administrator`, `BillingClerk`, `Auditor`).
- `ClinicalAccessGuard` enforces care-relationship for clinical reads.
- Break-glass override requires **non-empty** justification header, is *always* audited, is
  visible on `/api/v1/reports/access-anomalies`.

### Auditing
- Every clinical read → `AuditEvent` with `Kind = PatientDataRead`, actor, patient external
  id, purpose, correlation id, break-glass flag.
- Every clinical write → `AuditEvent` with `Kind = PatientDataWrite`.
- Audit table is append-only. `AppDbContext.SaveChanges` throws on any Modified/Deleted
  `AuditEvent`.

### Data protection
- Data at rest: SQLite file uses OS-level file permissions in dev; a production Postgres
  deployment relies on the DB's own at-rest encryption. Not evaluated for compliance.
- Data in transit: HTTPS is the deployment expectation (`app.UseHttpsRedirection()`);
  local dev may use HTTP for convenience.

### Rate limiting
- Fixed-window per-user via `AddRateLimiter`. 200 requests/minute default.

### Input validation
- FluentValidation / minimal-api parameter binding + `ValidationProblemDetails` at 422.
- Vitals unit + plausibility range enforced in `VitalReading.Create`.
- Cancellation reason enum bounded.

### Field-level minimisation
- Patient list endpoint returns demographic fields only.
- Encounter GET only returns clinical detail when access is allowed.

## 4. Things this project deliberately does NOT do

- **Does not claim HIPAA, GDPR, or ISO compliance.** No such assessment was performed.
- **Does not implement a real IdP.** Dev-token endpoint is a testing convenience.
- **Does not implement end-to-end encryption** of notes at rest.
- **Does not implement key rotation** for the JWT signing key.
- **Does not include SOC 2 controls** (change management, vendor management, employee
  screening) — those are process controls, out of scope.

## 5. Threat-model gaps to close before real use

1. Replace dev-token with an OpenID Connect provider (Entra ID, Auth0, Keycloak).
2. Add DB-at-rest encryption on the production Postgres instance.
3. Add JWT signing-key rotation with kid header selection.
4. Add web-app firewall / API gateway with per-tenant rate limits.
5. Perform a real threat model with the deploying organisation.
6. Perform a data-protection impact assessment (DPIA / equivalent).
