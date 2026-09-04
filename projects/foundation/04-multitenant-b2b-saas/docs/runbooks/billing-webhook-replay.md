# Runbook — Billing Webhook Replay or Forgery

## Trigger

- Duplicate webhook returns `{ "processed": false, "duplicate": true }`.
- Signature is invalid/malformed or timestamp is outside tolerance.
- Repeated event IDs or unusual failure bursts appear.

## Immediate actions

1. Capture event ID, timestamp, source network metadata and correlation ID.
2. Never log or paste the webhook secret.
3. Check `WebhookReceipts` for the event ID.
4. Confirm the organization's failure count/status changed at most once.
5. Rate-limit or block the sender at the edge if traffic is abusive.

## Classification

| Observation | Meaning |
|---|---|
| Valid signature, existing event ID | normal retry/replay; idempotent no-op |
| Valid signature, new event ID | process once |
| Invalid signature | possible forgery, proxy/body mutation or wrong secret |
| Stale timestamp | delayed replay or clock skew |
| Valid event for unknown tenant | configuration/integration defect; reject |

## Recovery

### Legitimate duplicate

- Return success/duplicate so the provider stops retrying.
- No tenant state repair is needed.

### Wrong or rotated secret

1. Compare key identifiers through the secret manager (production design).
2. Rotate using overlapping old/new verification windows.
3. Replay only original vendor events with new unique delivery evidence according to vendor guidance.

### Suspected forgery

1. Rotate the webhook secret.
2. Preserve request and receipt metadata without secret/body overexposure.
3. Audit tenant status transitions during the suspected window.
4. Correct state only through signed compensating events or a reviewed admin operation.

## Validation

- Re-send the same valid event: `processed=false`, no additional failure count.
- Change one body byte without resigning: HTTP 403.
- Sign a timestamp older than tolerance: HTTP 403.
- Send a fresh valid payment-success event: tenant becomes Active.

## Escalation

Escalate as a security incident if an invalid signature mutates state, a duplicate advances dunning, or replay affects a different tenant.
