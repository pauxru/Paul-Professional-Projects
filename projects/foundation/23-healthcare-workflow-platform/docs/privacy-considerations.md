# Privacy considerations

> ⚠️ **No HIPAA/GDPR compliance or certification is claimed, and no formal assessment has been
> performed.** This document uses the vocabulary of privacy law to describe *design
> considerations*, not to imply certification or legal fitness.
>
> ⚠️ **All data in this project is synthetic.** Fictional patients such as `Njeri Wanjiku` and
> `Kiprop Cheruiyot` at the `Nairobi Demo Clinic (fictional)` never existed.

## 1. Personal data touched

- Patient identifying data (name, DOB, phone, email, address in a real deployment).
- Clinical data (notes, vitals, referrals) — special category data.
- Staff identifying data (clinician name, actor id in audit rows).
- Access log (who read what) — itself personal data about staff.

## 2. Data minimisation

- **Field-level minimisation on list endpoints.** `GET /api/v1/patients` returns
  demographic/scheduling fields only. Clinical data lives on encounter endpoints and is
  gated by ABAC + audit.
- **Query minimisation.** Endpoints paginate; slot search filters by date; audit endpoint
  is Auditor-only.
- **Storage minimisation.** No copies of clinical text are kept outside `clinical_notes`;
  no full-text search index of notes is built by default.

## 3. Purpose limitation

Every audit event records a `Purpose` string tied to the endpoint call (e.g. `appointment.book`,
`encounter.note.add`, `encounter.read`). Purposes are documented in `Healthcare.Application`
service methods.

## 4. Retention

The platform is designed to support retention policies but does not enforce any hard
retention automatically. Recommendations:

- Appointments and audit rows: retain per applicable jurisdiction (e.g. 7 years for adult
  outpatient records is common; verify per deployment).
- Reminders: retain for 30 days after appointment.
- Waitlist entries: 6 months.

Implementation hook: `Reminder` and `WaitlistEntry` both carry `CreatedAtUtc`; a scheduled
task can archive/delete rows older than the policy without touching clinical data.

## 5. Consent

`Patient.Consents` stores per-purpose consent grants: `ReminderCommunication`,
`ResearchData`, `Marketing`. Reminder scheduling honours the consent flag; if not granted
or if `OptedOutOfReminders = true`, no reminders are queued.

## 6. Pseudonymisation options

- Every `Patient` has a `PatientId` value object (fictional check-digit scheme).
- Every audit row records the external `PatientId.Value`, not the internal Guid,
  simplifying pseudonymised export for statistical use.
- Reporting endpoints (utilisation, DNA statistics) work off appointment counts and do not
  return names.

## 7. Audit of reads

Every read of clinical data emits `AuditEvent(Kind=PatientDataRead)` including:
- Actor id + role.
- Purpose string.
- Patient external id.
- Correlation id (for cross-log tracing).
- Break-glass flag + justification (if applicable).
- VIP flag on the patient (surfaces on the anomaly report).

Audit rows are append-only at the persistence layer.

## 8. Access anomaly detection

`GET /api/v1/reports/access-anomalies` returns:
- All break-glass events in the window.
- Any patient-read where the actor had no matching care relationship (defensive check
  independent of the guard).
- Any VIP-patient read.
- Any out-of-hours read (before 06:00 or after 22:00 site local time).

## 9. Data-subject rights (support required in a deployment)

- **Right of access:** an `Auditor` endpoint could compile a subject-access report from the
  operational tables; not implemented here.
- **Right to rectification:** demographic amendment via `PATCH /api/v1/patients/{id}` (not
  implemented; note-style rectification would go through an amendment).
- **Right to erasure:** clinical data usually falls under a legal retention obligation
  that overrides erasure; policy must be defined per deployment.
- **Right to portability:** the audit + patient + appointment tables would form the export;
  not implemented.

## 10. Explicit non-claims

- **No HIPAA compliance is claimed.** No HIPAA Security Rule or Privacy Rule assessment has
  been performed. This project is a case study in the design *practices* often associated
  with those rules — not a compliant product.
- **No GDPR compliance is claimed.** No Data Protection Impact Assessment (DPIA) has been
  performed. Design mentions of purpose limitation, minimisation, and consent are illustrative.
- **No ISO 27001 certification is claimed.** No management-system audit has been performed.
- **No penetration test** has been performed on this codebase.
