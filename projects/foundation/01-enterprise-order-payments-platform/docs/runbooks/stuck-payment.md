# Runbook — stuck payment

**Symptom.** An `Order` sits in `AwaitingPayment` for longer than expected.
The associated `PaymentIntent` has `Status = Requires` (never authorized) or
`Status = Authorized` (never captured), and the customer is either being
double-billed on retry or getting no confirmation.

## Triage — 60-second check

1. Note the correlation id from the customer report or from the last request
   log entry.
2. Query the intent:
   ```powershell
   $t = (Invoke-RestMethod -Method Post -Uri http://localhost:5001/api/v1/auth/token `
       -ContentType 'application/json' `
       -Body '{"subject":"ops","scopes":["orders:write"]}').accessToken
   $intent = Invoke-RestMethod -Uri "http://localhost:5001/api/v1/payments/$intentId" `
       -Headers @{ Authorization = "Bearer $t" }
   $intent
   ```
3. Read `Attempts`. Each attempt records the outcome the provider reported.
4. Grep the app log for the correlation id — this shows every span emitted
   for that customer's flow (`order.place`, `payment.authorize`).

## Case A — intent in `Requires` (never authorized)

Most likely the provider returned `Timeout` and the retry did not recover.

1. Confirm the `Attempts` array has one or more entries with
   `Outcome = "Timeout"`.
2. Look at the correlation-id span for `payment.authorize` — you should see
   the Polly retry attempts. Polly retries twice with exponential + jitter
   backoff before giving up.
3. Issue a retry:
   ```powershell
   Invoke-RestMethod -Method Post `
       -Uri "http://localhost:5001/api/v1/payments/$intentId/retry-authorize" `
       -Headers @{ Authorization = "Bearer $t" }
   ```
4. If retries continue to time out, the provider is unreachable — page the
   on-call for the provider. Meanwhile leave the intent alone; do NOT
   manually void it, because the provider might yet callback.

## Case B — intent in `Authorized` (never captured)

Either the capture call was never issued (client bug) or the capture
succeeded provider-side but our webhook did not arrive.

1. Compare the provider settlement file for this window with the
   `PaymentIntents` table — trigger a reconciliation run:
   ```powershell
   Invoke-RestMethod -Method Post `
       -Uri "http://localhost:5001/api/v1/reconciliation/runs" `
       -Headers @{ Authorization = "Bearer $t"; 'Content-Type' = 'text/csv' } `
       -InFile settlement.csv
   ```
2. If the run reports `MissingInternally` for this intent — the provider
   captured but our webhook was lost. Issue the capture ourselves:
   ```powershell
   Invoke-RestMethod -Method Post `
       -Uri "http://localhost:5001/api/v1/payments/$intentId/capture" `
       -Headers @{ Authorization = "Bearer $t" }
   ```
3. Verify the audit trail records both the manual capture (with actor = the
   on-call user) and the eventual webhook arrival (with actor = `webhook`).

## Case C — Customer says they were double-billed

This should never happen — the `Idempotency-Key` middleware prevents it — but
if the customer's client omitted the header:

1. Look for two adjacent `PaymentIntents` with the same `OrderId`.
2. The second must be in `Failed` or `Voided`. If it is `Captured`, escalate:
   this is a bug in the client OR in our idempotency guard.
3. Refund the extra capture. Refund path is fully idempotent.

## Never do

- Never `UPDATE PaymentIntents SET Status = 'Captured'` by hand — this
  bypasses the aggregate's transition guard, the ledger write, and the
  outbox event. Always call the API endpoint.
- Never delete a `PaymentIntent` — the audit trail links to it by id.

## After-action

- File a follow-up ticket describing the correlation id, the flow, and the
  intervention. If the same provider outcome caused two incidents, tune the
  Polly configuration (retry count, timeout, or circuit-breaker window).
