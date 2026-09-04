# Runbook: Break-Glass Admin Operation

## When to run

Only when a genuine, time-critical, otherwise-unreachable admin action must be performed
and either (a) no authorised administrator is currently reachable, or (b) the required
approver is unreachable and the customer impact of waiting exceeds the risk of an audited
elevation.

Examples of legitimate use:

- Immediately disabling a partner when active fraud is confirmed and the responsible
  admin is offline.
- Rotating a signing key that is known compromised, out of hours.

## Preconditions

- Two distinct human identities: the **requester** (needs the action) and the
  **approver** (a separate person, ideally the on-call security lead).
- A written justification of at least 10 characters.
- Both parties are contactable and able to review the audit record afterwards.

## Steps

1. **Requester submits the request.**

   ```powershell
   $tok = "<requester admin bearer with step-up>"
   Invoke-RestMethod -Method Post -Uri http://localhost:5007/api/v1/admin/break-glass/request `
     -ContentType "application/json" `
     -Headers @{ Authorization = "Bearer $tok" } `
     -Body (@{
       action = "disable-partner:ACME-TREASURY"
       justification = "Confirmed fraud from partner ACME-TREASURY at 03:12 UTC; incident-1234"
       windowMinutes = 15
     } | ConvertTo-Json)
   # Response: { grantId, requester, expiresAt }
   ```

2. **Approver approves.**

   The approver **must not be the requester**. The domain enforces this: a grant with
   `Requester == Approver` is rejected by the constructor invariant.

   ```powershell
   $atok = "<approver admin bearer with step-up>"
   Invoke-RestMethod -Method Post -Uri http://localhost:5007/api/v1/admin/break-glass/approve `
     -ContentType "application/json" `
     -Headers @{ Authorization = "Bearer $atok" } `
     -Body (@{ grantId = "<from step 1>" } | ConvertTo-Json)
   ```

3. **Requester uses the grant.**

   The elevated action is performed *within the grant window*; a grant used outside its
   window returns 403.

   ```powershell
   Invoke-RestMethod -Method Post -Uri http://localhost:5007/api/v1/admin/break-glass/use `
     -ContentType "application/json" `
     -Headers @{ Authorization = "Bearer $tok" } `
     -Body (@{ grantId = "<from step 1>" } | ConvertTo-Json)
   # A single grant can only be used once. The domain flips UsedAtUtc on use.
   ```

4. **Post-incident review.**

   Every break-glass request, approval and use is written to the audit trail with kind
   `BreakGlassRequested` / `BreakGlassApproved` / `BreakGlassUsed`. Verify that the
   trail exists and the chain is intact:

   ```powershell
   Invoke-RestMethod -Uri "http://localhost:5007/api/v1/admin/audit?kind=BreakGlassUsed&take=10" -Headers @{ Authorization = "Bearer $tok" }
   Invoke-RestMethod -Uri http://localhost:5007/api/v1/admin/audit/verify-chain -Headers @{ Authorization = "Bearer $tok" }
   ```

## Rollback

Break-glass cannot be "rolled back" — it is a one-way audited elevation. The consequences
of the elevated action (partner disabled, key rotated, etc.) follow their own rollback
runbooks. What can be rolled back is: an *unused* grant can be revoked before its window
expires:

```powershell
Invoke-RestMethod -Method Post -Uri http://localhost:5007/api/v1/admin/break-glass/revoke `
  -ContentType "application/json" `
  -Headers @{ Authorization = "Bearer $atok" } `
  -Body (@{ grantId = "<...>"; reason = "no longer needed" } | ConvertTo-Json)
```

## Verification

- Request without approver → grant exists but is not usable.
- Attempted use with `requester == approver` → 422 (domain rule).
- Attempted use outside the window → 403.
- Attempted second use of the same grant → 403.
- All events on the audit trail; chain verifies clean.

## In a real deployment

- Break-glass is paged; the on-call security lead is auto-invited to approve.
- Every break-glass event is CC'd to the security-review inbox and triggers a
  blameless post-incident review within 24h.
- The window is auto-shortened for higher-risk actions.
