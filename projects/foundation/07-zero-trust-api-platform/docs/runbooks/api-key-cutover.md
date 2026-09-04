# Runbook: API-Key Migration Cutover

## When to run

At the end of the dual-accept phase for a partner integration, when all partners have
migrated to OAuth2 client-credentials and the last remaining API keys are ready to be
enforcement-rejected.

## Preconditions

- You have an admin bearer with `admin.users` scope and step-up.
- The migration report shows either (a) no keys used in the last N days, or (b) the
  remaining users have been notified of the cutover window and have not objected.
- A second admin is on hand for rollback approval if needed.

## Steps

1. **Review the migration report.**

   ```powershell
   $tok = "<admin bearer>"
   Invoke-RestMethod -Uri http://localhost:5007/api/v1/admin/api-keys/migration-report `
     -Headers @{ Authorization = "Bearer $tok" }
   # Response: array of { keyId, ownerPartnerCode, lastUsedAtUtc, usageCount, deprecatedAfterUtc, isPastDeprecation }
   ```

   For each key past its deprecation date and still being used, either:
   - Push the deprecation date out and notify the partner again, or
   - Contact the partner and coordinate cutover, or
   - Accept that the partner will fail closed at cutover (rare; only for consciously
     decommissioned integrations).

2. **Flip the enforcement toggle.**

   ```powershell
   Invoke-RestMethod -Method Post -Uri http://localhost:5007/api/v1/admin/api-keys/cutover `
     -ContentType "application/json" `
     -Headers @{ Authorization = "Bearer $tok" } `
     -Body (@{ enforcementActive = $true } | ConvertTo-Json)
   ```

   From this moment, any request whose API key is past its deprecation date is rejected
   with `api_key_past_cutover` (audited).

3. **Watch for cutover rejections.**

   ```powershell
   Invoke-RestMethod -Uri "http://localhost:5007/api/v1/admin/audit?kind=ApiKeyRejectedPostCutover&take=20" `
     -Headers @{ Authorization = "Bearer $tok" }
   ```

   If a partner is unexpectedly affected, either roll back the toggle (below) or push
   the specific key's `DeprecatedAfterUtc` forward.

4. **Communicate.**

   Post to the partner notification channel: "Cutover applied at HH:MM UTC. If any of
   your traffic is now returning 401, review the cutover runbook and contact your
   integration lead."

## Rollback

If the cutover was applied prematurely and rollback is required:

```powershell
Invoke-RestMethod -Method Post -Uri http://localhost:5007/api/v1/admin/api-keys/cutover `
  -ContentType "application/json" `
  -Headers @{ Authorization = "Bearer $tok" } `
  -Body (@{ enforcementActive = $false } | ConvertTo-Json)
```

Rollback restores dual-accept immediately. Requests with API keys past their deprecation
date now succeed and are flagged (audit kind `ApiKeyDeprecatedUsed`) — a temporary
compromise, not a permanent state.

## Verification

- After cutover: request with a live legacy key returns 401 with `api_key_past_cutover`.
- After cutover: request with an OAuth2 bearer succeeds normally.
- Migration report shows a shrinking list of "past deprecation" keys.
- Audit chain verifies clean throughout.

## In a real deployment

- The toggle is a feature-flag (LaunchDarkly / ConfigCat / Azure App Configuration), not
  a singleton in one process. The flip propagates to all instances.
- The cutover date is announced in advance in the partner developer portal, and the API
  returns a `Sunset` header on every request that used a legacy key.
- Post-cutover, the migration report and audit stream are watched for 24h; the on-call
  is the rollback authority.
