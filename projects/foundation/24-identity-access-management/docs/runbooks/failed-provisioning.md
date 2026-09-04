# Runbook — Failed Provisioning and Quarantine

## Trigger

- provisioning result is `Quarantined`;
- quarantine report contains unresolved items;
- target account differs from approved IGA state;
- lifecycle workflow completes internal revocation but target disable fails.

## Triage

1. Confirm `/health/ready` is healthy.
2. List quarantine:

   ```powershell
   Invoke-RestMethod -Headers $headers `
     http://localhost:5024/api/v1/provisioning/quarantine
   ```

3. Record job ID, connector key, user ID, operation, attempt count, last error, correlation ID, and age.
4. Classify:
   - transient target availability/throttling;
   - invalid/missing authoritative attribute;
   - permission mapping drift;
   - target-side conflict;
   - connector credential/authorization failure.

## Containment

- For leaver/compromise disable failures, disable the account directly in the target under incident/change control, then reconcile.
- Do not grant broad connector credentials to make the error disappear.
- Do not delete quarantine or audit rows.

## Retry

After correcting the root cause, replay the intended operation through the API:

```powershell
$body = @{ operation = 'Update' } | ConvertTo-Json
Invoke-RestMethod -Method Post -Headers $headers -ContentType application/json `
  -Uri http://localhost:5024/api/v1/provisioning/$connectorKey/users/$userId `
  -Body $body
```

Use `Create`, `Update`, `Disable`, or `Delete` to match the quarantined operation. The service creates a new job and retains the failed one for evidence.

## Reconciliation verification

```powershell
Invoke-RestMethod -Method Post -Headers $headers -ContentType application/json `
  -Uri http://localhost:5024/api/v1/provisioning/reconcile `
  -Body (@{ connectorKey = $connectorKey } | ConvertTo-Json)
```

Success criteria:

- expected account exists/enabled state matches lifecycle status;
- no orphan for the user;
- no rogue grant;
- new provisioning job is `Succeeded`;
- authorization/access profile matches approved desired access.

## Escalation

| Condition | Escalate to |
|---|---|
| Three transient failures / target outage | application operations owner |
| Authentication/credential failure | connector platform owner and security |
| Repeated mapping drift | entitlement catalogue owner |
| Leaver disable failure | incident commander immediately |
| Growing quarantine backlog/SLA breach | IGA service owner and governance lead |

## Post-incident

- Document cause and corrective action.
- Add a deterministic connector test for any mapping/retry regression.
- Review connector least-privilege scopes.
- Consider backoff, circuit-breaker, and queue capacity changes for production adapters.
