# Runbook — reconciliation mismatch

**Symptom.** The scheduled reconciliation run reports a non-zero number of
discrepancies against the provider settlement file. Or the ops dashboard
shows `MissingInternally` or `AmountMismatch` counts trending non-zero.

## Triage

1. Fetch the most recent run:
   ```powershell
   Invoke-RestMethod http://localhost:5001/api/v1/reconciliation/runs?page=1&pageSize=1 `
       -Headers @{ Authorization = "Bearer $token" }
   ```
2. Fetch the discrepancy rows for that run:
   ```powershell
   Invoke-RestMethod http://localhost:5001/api/v1/reconciliation/runs/$runId `
       -Headers @{ Authorization = "Bearer $token" }
   ```
3. Group discrepancies by `Kind` and start with the most damaging class.

## Handling by class

### `MissingInProvider`

Our records say we captured but the provider does not know about it.

- Likeliest cause: we authorized against the provider but the customer's
  final capture never happened, or the capture happened after the
  settlement file was generated (timing).
- **Do**: wait for the next settlement file; if still missing, escalate to
  the provider with the `ProviderReference` from the intent.
- **Do not**: manually `MarkFailed` the intent unless the provider
  formally confirms the transaction was rejected. Prematurely failing an
  intent that the provider quietly captured causes a double-refund.

### `MissingInternally`

The provider says a payment happened that we have no record of.

- Likeliest cause: our webhook was lost, or the provider generated a
  duplicate transaction, or (in a real fraud scenario) an attacker was able
  to submit a payment against a stolen merchant id.
- **Do**: check the `WebhookReplayRecords` table for the corresponding
  signature timestamp — if present, we saw the webhook but rejected it
  (probably the timestamp was outside the tolerance window).
- **Do**: contact the provider with the `ProviderReference` and confirm
  ownership before crediting the customer.
- **Do**: if legitimate, manually reproduce the capture by POSTing the
  captured intent to the API with the correct `IdempotencyKey` and the
  provider reference — this creates the missing record and the ledger
  entry.

### `AmountMismatch`

Our amount and the provider's amount differ.

- Likeliest cause: currency-conversion rounding, or a partial refund not
  yet reconciled.
- **Do**: check the `Refunds` and `LedgerEntries` tables for the intent —
  the net (`Captured - Refunds`) should equal the provider net.
- **Do**: if the mismatch is a rounding error consistent across many rows,
  file a bug against the currency-conversion configuration.

### `DuplicateInProvider`

The provider reported multiple settlement rows for the same intent id.

- Likeliest cause: a provider bug or a retry loop on their side.
- **Do**: sum the rows; if the total matches our captured amount, this is
  a display-only duplication and can be ignored (record why in the audit).
- **Do**: if the total exceeds our captured amount, escalate to the
  provider — likely a customer double-charge.

### `StatusMismatch`

Both sides know about the payment but disagree on the state.

- Likeliest cause: a webhook was lost, or the provider re-processed a
  refund we already handled.
- **Do**: re-run the reconciliation after the next settlement window; if
  still mismatched, follow the "missing webhook" playbook (see
  `stuck-payment.md`, Case B).

## Never do

- Never delete a `ReconciliationRun` — it is the primary evidence of what
  we saw when.
- Never edit `ReconciliationDiscrepancy` rows in place. Add an
  `AuditEvent` explaining the resolution and mark the row resolved via a
  future column (not implemented here but recommended).

## After-action

- If the discrepancy rate is trending up, tune the provider integration:
  add automatic retries for webhook delivery, tighten the tolerance
  window, or add a "captured but no webhook yet" watchdog.
- If any `MissingInternally` was a real payment, file a security ticket
  regardless — the fact that a payment happened without our system's
  knowledge is a signal worth investigating.
