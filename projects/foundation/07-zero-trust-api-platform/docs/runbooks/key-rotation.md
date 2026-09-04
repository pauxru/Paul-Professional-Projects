# Runbook: Key Rotation

## When to run

- Scheduled — every 90 days as part of routine hygiene.
- On demand — if a key is suspected of being compromised, or immediately after a
  personnel change on the trust-core operator group.

## Preconditions

- You are authenticated to the admin API with `admin.audit + admin.users` scopes and
  step-up (`amr=mfa`, `acr=urn:ntsf:acr:step-up`).
- The audit log verify-chain endpoint currently returns `{ ok: true }`. If it does not,
  stop and investigate the audit chain first; a compromised audit trail invalidates any
  attribution of the rotation.
- A second operator is available to approve any break-glass actions that arise.

## Steps

1. Confirm current JWKS state.

   ```powershell
   Invoke-RestMethod http://localhost:5007/.well-known/jwks.json
   # Note the current primary kid and any secondary kids.
   ```

2. Trigger a rotation.

   ```powershell
   $tok = "<admin bearer token with step-up>"
   Invoke-RestMethod -Method Post `
     -Uri http://localhost:5007/api/v1/admin/keys/rotate `
     -Headers @{ Authorization = "Bearer $tok" }
   # Response includes: new primary kid + list of currently active kids.
   ```

3. Confirm the new state.

   ```powershell
   Invoke-RestMethod http://localhost:5007/.well-known/jwks.json
   # Two active keys should be present: the new primary and the previous primary
   # (now secondary). Older keys are retired and not published.
   ```

4. Verify that tokens signed with either active key still validate.

   ```powershell
   # Get a fresh token (signed with new primary):
   $body = @{ grant_type = "password"; username = "alice"; password = "CustomerPassw0rd!"; audience = "ntsf-customer-api"; scope = "customer.read" } | ConvertTo-Json
   $new  = Invoke-RestMethod -Method Post -Uri http://localhost:5007/api/v1/auth/token -ContentType "application/json" -Body $body
   Invoke-RestMethod -Uri http://localhost:5007/api/v1/customer/accounts -Headers @{ Authorization = "Bearer $($new.access_token)" }
   # Existing tokens (signed with previous primary, now secondary) also still work
   # until their exp.
   ```

5. Record the rotation.

   The admin endpoint automatically writes an audit record (kind `KeyRotated`) with the
   old primary kid and the new primary kid. Confirm:

   ```powershell
   Invoke-RestMethod -Uri "http://localhost:5007/api/v1/admin/audit?take=5" `
     -Headers @{ Authorization = "Bearer $tok" }
   ```

## Rollback

If a rotation was performed in error and the previous key must be reinstated as primary:

1. Retrieve the retired key's kid from the audit log.
2. `POST /api/v1/admin/keys/reinstate` with the old kid (implementation left as an
   extension point; this repository intentionally does not expose reinstate to reduce
   accident risk).
3. In the current build, the safest rollback is: run another rotation (issues yet another
   new key) and let existing tokens ride out their `exp`. Never publish a key that has
   been suspected of compromise; issue a new one instead.

## Verification

- `GET /.well-known/jwks.json` returns two active keys with distinct `kid`s.
- A fresh access token signed with the new key validates on every surface.
- Existing tokens signed with the previous key still validate until expiry.
- Audit record `KeyRotated` exists and the verify-chain endpoint still returns
  `{ ok: true }`.

## In a real deployment

- Signing keys live in Azure Key Vault / AWS KMS, not the app database.
- The rotation trigger is a scheduled Azure Function / Lambda invoking the admin endpoint
  with a workload identity, not a human bearer token.
- The retired key is removed from JWKS *after* a documented grace window equal to the
  longest access-token lifetime (5 minutes for admin; 15 minutes for customer). Retiring
  earlier will disconnect users mid-session.
