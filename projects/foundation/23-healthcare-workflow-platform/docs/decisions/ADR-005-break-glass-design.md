# ADR-005 — Break-glass design

**Status:** Accepted
**Date:** 2026-09-01

## Context
Real hospitals must let clinicians read the records of patients they are not currently
treating in genuine emergencies (walk-in, transfer, code, etc.). ABAC alone (care
relationship) blocks this. The industry pattern is a **break-glass** override: any
authenticated clinician can force access, but the event is audited, alerted, and reviewed.

## Options
1. **No break-glass:** blocks legitimate emergencies; unacceptable.
2. **Break-glass by dedicated role:** doesn't scale (every clinician would need the role).
3. **Break-glass on demand via header (this ADR):** any `Clinician` may set the header
   with a justification; guard allows and audits.
4. **Break-glass via approval workflow:** every request needs pre-approval; unacceptable
   latency for emergencies.

## Decision
Two request headers:
- `X-Break-Glass: true`
- `X-Break-Glass-Justification: <non-empty text>`

Rules:
1. Both headers required; missing or empty justification → 403.
2. `ClinicalAccessGuard` bypasses the care-relationship check and returns
   `AccessDecision(allowed=true, reason="break_glass", breakGlass=true)`.
3. The endpoint records an `AuditEvent` with `BreakGlass = true` and the justification
   trimmed to 512 chars.
4. Every break-glass event increments the `healthcare.break_glass.total` counter and
   surfaces on `GET /api/v1/reports/access-anomalies`.
5. VIP patient reads are also flagged as an anomaly for review even when a care
   relationship exists.

Test coverage: `AccessControlTests.Break_Glass_Grants_Access_And_Is_Audited_And_Flagged_As_Anomaly`
and `Missing_Break_Glass_Justification_Denies_On_Guard_Level`.

## Consequences
- Latency for real emergencies stays at "one HTTP call".
- Every break-glass event is discoverable during clinical governance review.
- Deterrent effect: staff know the event is visible.

## Risks
- Header spoofing: mitigated by requiring an authenticated clinician role; unauthenticated
  requests receive 401 before the guard runs.
- Alert fatigue on the anomaly report if break-glass becomes routine: expected to be rare;
  the report is designed for weekly review.

## Alternatives considered
- Approval workflow: rejected for latency.
- Break-glass role: rejected for scalability; every clinician would need it.
