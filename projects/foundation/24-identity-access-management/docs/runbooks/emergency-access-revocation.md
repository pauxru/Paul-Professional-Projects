# Runbook — Emergency Access Revocation

## Trigger

- suspected account compromise;
- malicious or mistaken JIT elevation;
- urgent leaver notification;
- critical policy/SoD finding requiring immediate containment.

## Preconditions

- Incident ticket exists.
- Operator has `iga.admin`; approval actions additionally require `iga.approve`.
- API readiness is healthy at `/health/ready`.

## Immediate containment

1. Obtain an administration token from the configured production OIDC provider. The local token endpoint is development-only.
2. Retrieve the user access profile:

   ```powershell
   Invoke-RestMethod -Headers $headers `
     http://localhost:5024/api/v1/reports/users/$userId/access-profile
   ```

3. List active/pending elevations and revoke every affected elevation:

   ```powershell
   Invoke-RestMethod -Method Post -Headers $headers -ContentType application/json `
     -Uri http://localhost:5024/api/v1/elevations/$elevationId/revoke `
     -Body '{"reason":"Emergency containment under INC-1234"}'
   ```

4. If the identity itself must be disabled, execute the leaver transition:

   ```powershell
   Invoke-RestMethod -Method Post -Headers $headers `
     http://localhost:5024/api/v1/users/$userId/leaver
   ```

   This terminates the identity/session marker, revokes role/direct/group/JIT paths, disables target accounts, and reconciles.

5. If a permission must be denied estate-wide, author a narrow explicit deny, simulate it, peer-review the gained/lost result, then activate through the controlled policy-change process. Do not create an unbounded wildcard deny during an incident without blast-radius review.

## Verification

Evaluate every sensitive permission with the incident context:

```powershell
$body = @{
  userId = $userId
  permission = 'app:finance/payment:approve'
  resource = @{}
  environment = @{ networkZone='Unknown'; deviceTrust='Untrusted'; mfaLevel=0 }
} | ConvertTo-Json -Depth 5
Invoke-RestMethod -Method Post -Headers $headers -ContentType application/json `
  -Uri http://localhost:5024/api/v1/authz/evaluate -Body $body
```

Expected: `allowed=false`. Confirm there is no active elevation and that the access profile no longer shows the permission.

Run reconciliation:

```powershell
Invoke-RestMethod -Method Post -Headers $headers -ContentType application/json `
  -Uri http://localhost:5024/api/v1/provisioning/reconcile `
  -Body '{"connectorKey":null}'
```

Investigate any residual orphan or rogue grant immediately in the target system.

## Evidence

Export audit records for the incident correlation ID, record the before/after decisions, connector findings, operator, incident ticket, and time of confirmation. Preserve external IdP/session logs in the incident record.

## Escalation

- Connector remains enabled: target application owner and security incident commander.
- Audit append fails: database/platform owner; stop non-essential governance changes.
- Policy deny causes unrelated outage: policy owner and change approver; narrow or disable the policy after simulation.

## Recovery

Restore access only after compromise clearance through a new request or JIT elevation. Do not edit revocation exclusions or audit records directly.
