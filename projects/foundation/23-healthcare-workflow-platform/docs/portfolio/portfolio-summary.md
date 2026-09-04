# Portfolio summary

**Project:** Healthcare Appointment & Clinical Workflow Platform (self-directed case study).

## What it is
A .NET 10 modular monolith that runs the operational and clinical workflow of a multi-site
outpatient clinic. It focuses on the hard parts: strict scheduling, provably-correct
concurrency, tightly scoped access control with a break-glass override, and full
auditability of every clinical read.

## Highlights (things I would defend at interview)

1. **Concurrent-booking correctness under contention** — a real parallel test proves exactly
   one of two simultaneous booking attempts on the same slot succeeds; the DB-level
   filtered unique index enforces it, application-level checks translate the constraint
   violation into a domain-shaped 409 response. This is the difference between a scheduling
   feature that "works in dev" and one that survives production.
2. **Break-glass as a first-class citizen.** Not a role, not a bypass — a documented flow
   that requires justification, is always audited, and is always visible on the anomaly
   report. Every hospital needs this; naïve role systems don't have it.
3. **Append-only clinical notes** enforced at *three* layers (domain, DbContext, API), with
   integration tests that write directly to the tracked entity and prove the DbContext
   rejects the update. Nothing is overwritten or deleted — an amendment is a new version.
4. **Timezone-correct scheduling** through a real DST transition. Storage in UTC, business
   logic in local, IANA tz on the facility, integration test verifies availability across
   the March DST switch in London.
5. **Every clinical read is audited** with actor, purpose, patient, correlation id, and
   break-glass flag; anomaly report calls out break-glass / no-care-relationship /
   out-of-hours / VIP reads for weekly review.

## Scale of implementation
- 4 assemblies (Domain / Application / Infrastructure / Api).
- ~5,000 lines of C#.
- 62 automated tests (29 unit + 33 integration) — all passing.
- 6 ADRs, security review, privacy considerations, database schema, scheduling model,
  three runbooks, five portfolio artefacts.

## What's synthetic and what isn't
- Every patient, clinician, facility, and clinical note is fictional.
- The scheduling engine, ABAC guard, audit sink, and reminder pipeline are real code with
  real tests. They run against a real EF Core context and a real SQLite database.

## What I explicitly do NOT claim
- HIPAA, GDPR, or ISO 27001 compliance. No such assessment has been performed. The
  vocabulary of those frameworks is used only to describe design considerations.

## Repository status
Local git repository. No push target. One commit with `Co-authored-by: Copilot`.
