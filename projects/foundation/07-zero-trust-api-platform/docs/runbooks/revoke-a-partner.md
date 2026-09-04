# Runbook: Revoke a Partner

## When to run

- Confirmed compromise of a partner's OAuth client secret or their runtime credentials.
- Detection of anomalous partner traffic (spike in denied requests, request from
  disallowed IP, requests without expected `X-Client-Cert-Thumbprint`, or unexpected
  countries).
- Partner offboarding.

## Preconditions

- You have an admin bearer with `admin.users` scope and step-up.
- You know the partner's `PartnerCode` (e.g. `ACME-TREASURY`).

## Steps

1. **Disable the partner.**

   Rotating the partner's client secret is not enough on its own — an attacker holding a
   live access token can still make requests until it expires. The partner must be
   disabled *and* their tokens must be added to the revocation list.

   ```powershell
   $tok = "<admin bearer>"
   Invoke-RestMethod -Method Post -Uri http://localhost:5007/api/v1/admin/partners/ACME-TREASURY/disable `
     -Headers @{ Authorization = "Bearer $tok" }
   ```

   The disabled flag makes `client_credentials` grants fail immediately for this partner.

2. **Revoke outstanding tokens.**

   For each live refresh token in the partner's family, add its `jti` to the revocation
   list:

   ```powershell
   Invoke-RestMethod -Method Post -Uri http://localhost:5007/api/v1/admin/tokens/revoke `
     -ContentType "application/json" `
     -Headers @{ Authorization = "Bearer $tok" } `
     -Body (@{ subject = "ACME-TREASURY"; reason = "partner_revoked" } | ConvertTo-Json)
   ```

   Any bearer whose `jti` is on the revocation list will fail validation on the next
   request.

3. **Revoke API keys owned by the partner.**

   If the partner is in the dual-accept phase, mark their API keys revoked:

   ```powershell
   Invoke-RestMethod -Method Post -Uri http://localhost:5007/api/v1/admin/api-keys/acme-legacy-key-01/revoke `
     -Headers @{ Authorization = "Bearer $tok" }
   ```

4. **Notify the partner.**

   Out of band. Include: incident time, why the credentials were revoked, what the
   partner should do (rotate secrets, reissue keys, re-attest their environment).

5. **Confirm.**

   - `POST /auth/token` with the partner's credentials returns `invalid_client`.
   - Any bearer from that partner returns 401.
   - The audit trail contains `PartnerDisabled` and `TokenRevoked` records.

## Rollback

If the partner was disabled in error:

```powershell
Invoke-RestMethod -Method Post -Uri http://localhost:5007/api/v1/admin/partners/ACME-TREASURY/enable `
  -Headers @{ Authorization = "Bearer $tok" }
```

Revoked tokens stay revoked (they are already unusable); the partner must obtain a new
access token via `client_credentials`.

## Verification

- New `client_credentials` request from the partner returns 200 (if re-enabled) or
  `invalid_client` (if kept disabled).
- Any previously issued bearer for that partner returns 401.
- Audit chain still verifies clean.

## In a real deployment

- This runbook is triggered from an on-call runbook — pager, ticket, timeline, everything.
- Notification is via an established out-of-band channel (phone / secure email).
- Root-cause and blameless post-mortem within 5 business days.
