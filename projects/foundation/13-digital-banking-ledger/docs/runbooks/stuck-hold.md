# Runbook — Stuck / Stale Hold

**Symptom**: a customer's **available** balance is lower than expected while their **cleared** balance
is correct; an authorization hold appears not to have been captured, released, or expired; or the
`ledger.holds.expired` counter has flat-lined while active holds are past their `ExpiresAt`.

## Background

A **hold** reduces available balance (`AvailableMinor = BalanceMinor − HeldMinor`) without moving
cleared funds. A hold leaves the `Active` state by exactly one of:

- **Capture** (full or partial) → posts an entry and releases any remainder.
- **Release** → frees the held amount.
- **Expiry** → the `HoldExpiryBackgroundService` sweeps holds whose `ExpiresAt` has passed (time from
  `IClock`) and releases them, incrementing `ledger.holds.expired`.

A "stuck" hold is one that should have left `Active` but hasn't, so available balance is understated.

## Severity

- Single customer, small amount → **Sev-3**.
- Many holds stuck (sweeper not running) → **Sev-2** (systemic available-balance understatement).

## Triage

1. **Locate the hold(s).** Using a `ledger:read` token, fetch the account balance (shows
   `heldMinor`) and, if known, the hold:
   ```powershell
   Invoke-RestMethod "$base/api/v1/accounts/$accountId/balance" -Headers $auth
   Invoke-RestMethod "$base/api/v1/holds/$holdId" -Headers $auth
   ```
   Note `status`, `amountMinor`, `capturedMinor`, `remainingMinor`, `expiresAt`.

2. **Is the background sweeper running?** Check logs for the `HoldExpiryBackgroundService` and the
   `ledger.holds.expired` metric. If the counter is not advancing and holds are past `ExpiresAt`, the
   hosted service may not be running (e.g. it is disabled in the test host, or the process is
   unhealthy). Confirm the API process is up (`/health`).

3. **Is the clock correct?** Expiry is driven by `IClock`. A wrong system clock (or an injected clock
   in a non-production environment) will make holds appear stuck. Verify host time.

## Resolution

- **Legitimately expired but not swept**: once the sweeper is healthy it will release the hold on its
  next pass. To resolve immediately, **release** it explicitly:
  ```powershell
  Invoke-RestMethod -Method Post "$base/api/v1/holds/$holdId/release" `
    -Headers ($postAuth + @{ "Idempotency-Key" = "manual-release-$holdId" })
  ```
- **Should have been captured**: capture it (full or partial); the remainder is released
  automatically:
  ```powershell
  Invoke-RestMethod -Method Post "$base/api/v1/holds/$holdId/capture" `
    -Headers ($postAuth + @{ "Idempotency-Key" = "manual-capture-$holdId" }) `
    -ContentType application/json -Body (@{ destinationAccountId = $dest; captureMinor = 12000 } | ConvertTo-Json)
  ```
- **Sweeper not running**: restart the API so the `HoldExpiryBackgroundService` is re-registered;
  confirm `ledger.holds.expired` begins advancing again.

All hold operations are **idempotent** — using the same `Idempotency-Key` for a manual release/capture
is safe if you have to retry.

## Verification

- Re-fetch the account balance; `heldMinor` should have dropped and `availableMinor` recovered.
- Confirm the hold `status` is now `Captured`, `PartiallyCaptured`, `Released`, or `Expired`.
- Run `integrity-verification.md` to confirm cached balances still reconcile.

## Prevention

- Alert if any hold's `ExpiresAt` is more than a few minutes in the past while still `Active`.
- Alert if `ledger.holds.expired` stops advancing while active, past-expiry holds exist.
- Monitor the background service's health as part of the readiness signal.
