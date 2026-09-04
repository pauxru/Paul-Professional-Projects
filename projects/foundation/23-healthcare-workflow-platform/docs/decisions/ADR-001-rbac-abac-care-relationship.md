# ADR-001 — RBAC + ABAC (care-relationship) access control

**Status:** Accepted
**Date:** 2026-09-01

## Context
Clinicians must be able to read a patient's clinical record for patients they are treating and
must **not** be able to read the record of any other patient. This is a hospital control that
prevents casual browsing of clinical data by staff who happen to have a valid login. RBAC alone
cannot express it: two clinicians of the same role (`Clinician`) may have very different access
to any given patient depending on whether or not they have a therapeutic relationship.

## Options
1. **Pure RBAC:** grant clinicians blanket read access. Simple; unsafe for privacy.
2. **RBAC + external policy engine** (e.g. Open Policy Agent, Cerbos): powerful but adds a
   runtime dependency and out-of-process latency; overkill for a single monolith.
3. **RBAC + in-process ABAC (this ADR):** a single `ClinicalAccessGuard` evaluates a care
   relationship in-process against the same DbContext, with a break-glass override.
4. **Row-level security (RLS) in Postgres:** works only for Postgres; sqlite has no RLS.

## Decision
Combine ASP.NET Core role-based authorization (RBAC) with an in-process ABAC guard,
`ClinicalAccessGuard`, invoked by every endpoint that touches clinical data. A break-glass
header (`X-Break-Glass: true` + `X-Break-Glass-Justification: <non-empty>`) bypasses the care
relationship check but records the event as an anomaly.

The care relationship is defined as: **the requesting clinician has an appointment, an
encounter, or a referral with the target patient within the last N days** (default 90).

## Consequences
- Care-relationship queries run against the operational database — one extra query per
  clinical read. Acceptable at operational scale.
- The guard is testable — 7 integration tests cover the RBAC/ABAC matrix directly.
- Adding a new role or a new relationship type is a code change, not a policy change; that
  is acceptable given we control both the code and the deployment for a portfolio project.

## Risks
- If the DbContext is unavailable, every clinical read fails closed (403). We mitigate with
  health checks.
- Time-window off-by-one bugs could allow slightly-expired relationships. We add unit tests
  around the window boundary.

## Alternatives considered
- OPA sidecar: rejected for a portfolio project — deploy complexity outweighs benefit.
- Cerbos: same reasoning as OPA.
- Postgres RLS: rejected because SQLite is the default provider.
