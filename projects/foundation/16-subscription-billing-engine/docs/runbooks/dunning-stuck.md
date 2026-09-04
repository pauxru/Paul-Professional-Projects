# Runbook — Dunning Workflow Stuck

## Trigger

- A past-due invoice has no retry at its configured day.
- `billing.dunning.attempts` is flat while pending cases exist.
- A case remains pending after day 7, or access state disagrees with invoice/payment state.

## Triage

1. Capture invoice ID, subscription ID, case ID, correlation IDs, and current UTC time.
2. Confirm the dunning worker is running and `/health/ready` is healthy.
3. Inspect `initial_failure_at`, `attempts_completed`, `last_attempt_at`, `recovered`, and `escalated`.
4. Compare the next expected time to configured absolute offsets (default day 1, 3, 5, 7).
5. Review payment attempts by increasing attempt number and classification.
6. Check notification-hook logs separately; notification failure must not invent a payment result.

## Safe recovery

- If a retry is due and no attempt exists, run the dunning processor through the normal worker/manual operational path; preserve its idempotency key.
- If the provider reports success, send/replay a correctly signed `payment.succeeded` reconciliation webhook with a fresh nonce.
- Successful reconciliation should mark the invoice Paid, mark the case recovered, set the subscription Active, and clear access suspension.
- After exhausted failures, ensure invoice Uncollectible, subscription Unpaid, suspension true, and escalation notification present.

## Do not

- Do not reset attempt numbers or delete payment attempts.
- Do not mark an invoice Paid solely from a customer assertion.
- Do not reuse webhook nonces or bypass signature verification.
- Do not remove suspension without reconciling payment/invoice evidence.

## Escalation

Escalate provider timeouts with provider reference, idempotency key, and UTC timestamps. Escalate internal state divergence with a database snapshot and audit hashes. Use a corrective append-only event/credit note rather than mutating historical evidence.
