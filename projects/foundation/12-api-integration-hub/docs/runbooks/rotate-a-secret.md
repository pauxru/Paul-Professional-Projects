# Runbook — Rotate a Secret

## Preconditions

- Operator has `hub.admin`.
- New credential is active at the target or the target supports an overlap window.
- The master encryption key is available through protected configuration.

## Procedure

1. List metadata (values are never returned):

   ```powershell
   Invoke-RestMethod -Uri http://localhost:5012/api/v1/secrets -Headers $headers
   ```

2. Rotate:

   ```powershell
   $body = @{ name = "crm/apiKey"; value = "<new-value-from-secure-channel>" } | ConvertTo-Json
   Invoke-RestMethod -Method Post `
     -Uri "http://localhost:5012/api/v1/secrets/crm%2FapiKey/rotate" `
     -Headers $headers -ContentType application/json -Body $body
   ```

3. Run a read-only connector operation or a controlled flow.
4. Verify successful authentication and the incremented secret version.
5. Revoke the previous target credential after the overlap window.
6. Search logs/history to confirm the value was not emitted.

## Rollback

The local demonstrator keeps historical encrypted versions but exposes only current-version reads. If rotation fails, rotate again with the prior value. A production vault adapter should use its native version activation mechanism.
