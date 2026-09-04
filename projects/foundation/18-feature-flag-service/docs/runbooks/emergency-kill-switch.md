# Emergency Kill Switch Runbook

## Purpose
Use this runbook when a released feature must stop affecting traffic immediately. The kill-switch endpoint bypasses production approval intentionally and writes a `KillSwitchBypass` audit entry.

## Preconditions
- Confirm the incident and affected project/environment/flag.
- Obtain a short-lived token with `flags:write` from the production identity provider (the development token endpoint is not available in Production).
- Record the incident/ticket identifier in the comment/ticket fields.

## Action
```powershell
$headers = @{ Authorization = "Bearer $token" }
$body = @{ enabled = $false; comment = 'INC-123: stop erroneous checkout behavior'; ticketReference = 'INC-123' } | ConvertTo-Json
Invoke-RestMethod -Method POST -Uri 'https://flags.example/api/v1/projects/acme/environments/production/flags/new-checkout/kill-switch' -Headers $headers -ContentType 'application/json' -Body $body
```

## Verify
1. Use the targeting-preview endpoint with a known context; verify `reason.kind` is `Off` and `variationIndex` is the off variation.
2. Check `/api/v1/projects/acme/environments/production/audit` for `KillSwitchBypass`, actor, correlation ID, before/after snapshot, and ticket reference.
3. Verify consuming SDKs receive the SSE update or their next poll; monitor disconnected consumers separately.

## Recovery
After the incident, do not silently re-enable. Create a reviewed production approval request for the corrected configuration, then apply it. Retain the incident reference in audit history.
