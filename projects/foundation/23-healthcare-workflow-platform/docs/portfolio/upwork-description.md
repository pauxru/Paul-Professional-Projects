# Upwork / portfolio description

**Title:** Healthcare appointment & clinical workflow platform (case study).

**Body:**

Built a .NET 10 modular monolith modelling the scheduling and clinical-workflow engine of a
multi-site outpatient clinic. The project focuses on the areas of healthcare software that
are genuinely hard: strict scheduling constraints, provably-correct concurrency on shared
slots, tightly scoped access control with a break-glass override, and end-to-end
auditability of every read of clinical data.

Key engineering points:

- **Concurrent booking correctness.** DB-level filtered unique indexes on
  `(clinician_id, start_utc)` and `(room_id, start_utc)` filtered to non-terminal statuses;
  proven by a real parallel integration test.
- **RBAC + ABAC access control** with `Receptionist / Nurse / Clinician / ClinicalLead /
  Administrator / BillingClerk / Auditor` roles and an attribute check that requires a
  care relationship (appointment, encounter, or referral within a window) before clinical
  data is returned.
- **Break-glass override** by request header, requiring justification, always audited,
  visible on a weekly anomaly report.
- **Append-only clinical notes** enforced at domain, DbContext, and API layers.
  Amendments are new versions referencing the original by `RootNoteId`.
- **Timezone-correct scheduling** through DST transitions (verified with a London-facility
  DST test).
- **Full audit trail** — every clinical read and write is recorded with actor, role,
  patient external id, purpose, correlation id, and break-glass flag.
- 62 automated tests (29 unit + 33 integration), all passing on `dotnet test -c Release`.
- 6 ADRs, security review, privacy considerations, database schema, scheduling model,
  three runbooks.

**Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core 9 on SQLite (Postgres compatible),
JWT bearer authentication, ProblemDetails, OpenAPI, OpenTelemetry.

**Data:** All patients, clinicians, and facilities are fictional (e.g. `Nairobi Demo Clinic
(fictional)`). No HIPAA/GDPR/ISO 27001 compliance or certification is claimed and no
assessment has been performed.

**Deliverables:** working code, README, ADRs, runbooks, security review, privacy
considerations, database schema, scheduling model, portfolio artefacts, CI workflow,
demo script. Repository initialised locally.
