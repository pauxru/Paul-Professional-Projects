# Runbook — Break-glass review

## Purpose

Every break-glass access to a patient record must be reviewed within one working week.
This runbook describes the weekly review.

## Cadence
Weekly, on Monday morning.

## Preconditions
- You are logged in as an `Auditor`.

## Procedure

1. Pull the anomaly report for the last 7 days:
   ```
   GET /api/v1/reports/access-anomalies?fromUtc=<7d ago>&toUtc=<now>
   ```
   Filter rows where `breakGlass = true`.

2. For each row:
   - Note `actorId`, `patientExternalId`, `justification`, `correlationId`.
   - Correlate to the audit trail:
     ```
     GET /api/v1/audit?actorId=<actor>&patientId=<external>&fromUtc=<-7d>&toUtc=<now>
     ```
   - Confirm the justification is medically reasonable. If in doubt, ask the actor's
     line manager.

3. Categorise:
   - **Legitimate emergency** — record as reviewed with note `LEGIT`.
   - **Legitimate routine** (should have gone through the ABAC path but the actor lacked a
     registered care relationship due to a data issue) — record with note `LEGIT-DATAFIX`
     and open a ticket to correct the care-relationship data.
   - **Improper** — record with note `IMPROPER`, escalate to clinical governance.

4. File the weekly summary in the clinical governance system.

## Red flags

- Same actor triggering break-glass on the same patient repeatedly (may indicate the ABAC
  care-relationship window is misconfigured or that access should be granted via role).
- Break-glass on a VIP patient with a thin justification.
- Break-glass at 03:00 by a receptionist role (should not be possible; investigate token
  issuance).

## Related

- `docs/decisions/ADR-005-break-glass-design.md` for design rationale.
- `docs/security/security-review.md` for the STRIDE mapping.
