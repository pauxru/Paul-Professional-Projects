# Runbook — Underwriting Backlog

## Trigger
The `GET /api/v1/underwriting/queue` backlog grows, SLA due dates are breached, or the `loan.sla.breach.count` meter rises.

## Triage
1. Query queue pages ordered by priority and SLA due time.
2. Confirm locked items have not exceeded the 20-minute claim expiry; expired claims can be safely reclaimed.
3. Prioritize breached SLA items, then risk/value priority, while preserving required document and KYC gates.
4. Do not approve an item solely to clear backlog: delegated authority and four-eyes controls remain mandatory.

## Capacity response
Assign authorized Senior/Credit Committee underwriters, use the claim endpoint before a decision, and separate initial review from second approval for exposure above the configured threshold. Refer incomplete cases to `DocumentsPending` rather than inventing evidence.

## Exit and follow-up
Track oldest age, queue count, claims, referrals, decisions, and approval/referral meters until normal. After clearing, review recurring referral reasons and whether document/rule policy can be clarified. Do not weaken a rule without versioned what-if analysis and policy approval.
