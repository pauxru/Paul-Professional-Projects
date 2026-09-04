# Runbook — Unbalanced-Entry Alert

**Alert**: the `ledger.imbalance.attempts` counter is rising, or the trial balance does not sum to
zero in some currency, or a client reports repeated `422 ledger.unbalanced` / `ledger.mixed_currency`
responses.

## What it means

The engine **rejected** an attempt to post an entry whose debits did not equal its credits (per
currency), or that mixed currencies outside an FX entry. In normal operation this is the system
**working as designed** — the invariant is enforced in the domain, so an unbalanced entry can never be
persisted. A rising counter usually indicates a **buggy or misconfigured client**, not ledger
corruption.

A trial balance that is genuinely non-zero, by contrast, would indicate something far more serious
(cached-balance drift) and should be treated as a Sev-1.

## Severity

- Rising `ledger.imbalance.attempts` only, trial balance still zero → **Sev-3** (client bug).
- Trial balance non-zero in any currency, or integrity check unhealthy → **Sev-1** (integrity).

## Triage

1. **Confirm the ledger itself is still balanced.** Authenticate with a `ledger:read`/`ledger:admin`
   token and check:
   ```powershell
   Invoke-RestMethod "$base/api/v1/reports/trial-balance" -Headers $auth | Select-Object isBalanced
   Invoke-RestMethod "$base/api/v1/admin/integrity/verify" -Headers $adminAuth | Select-Object isHealthy
   ```
   - `isBalanced = true` and `isHealthy = true` → the ledger is intact; this is a client problem, go
     to step 2.
   - Either is `false` → escalate to **Sev-1** and follow `integrity-verification.md`.

2. **Identify the offending client.** The rejected requests carry a correlation id. Search structured
   logs for `ledger.unbalanced` / `ledger.mixed_currency` and group by `SourceSystem` /
   `CorrelationId` to find which caller is sending malformed entries.

3. **Inspect a sample payload.** Common causes:
   - Debit total ≠ credit total (a rounding or off-by-one bug in the caller).
   - A posting in the wrong currency, or a multi-currency entry sent to `/entries` instead of
     `/fx/convert`.
   - Amounts sent in **major** units (e.g. `1000.00`) instead of **minor** units (`100000`).

## Resolution

- Fix the client to send balanced, single-currency entries in **minor units**, using `/fx/convert`
  for cross-currency movements.
- No server-side change is needed for the common case — the rejection is correct behaviour.
- If the trial balance was genuinely non-zero, do **not** attempt manual edits (the ledger is
  append-only). Follow `integrity-verification.md`, identify the discrepant accounts from the
  reconciliation report, and post a correcting **reversal/adjustment** entry through the API after
  root-causing.

## Prevention

- Dashboard the `ledger.imbalance.attempts` counter by `reason` and by client source system.
- Alert on trial-balance `isBalanced = false` (should never fire) at Sev-1.
- Provide client teams the minor-unit contract and the `/fx/quote` preview endpoint.
