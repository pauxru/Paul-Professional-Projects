# Bad Rollout Rollback Runbook

## Detect and assess
Identify the flag, environment, variation, rollout weight, affected cohort, and correlation/incident ticket. Use analytics metrics and experiment summaries as diagnostic evidence only; they are not a substitute for business impact validation.

## Choose the least disruptive action
1. If impact is severe, use the emergency kill switch.
2. If only the new allocation is risky, reduce its weight or target only a safe segment through a production approval request.
3. If a known-good configuration existed, use the audit history’s one-click revert endpoint and document the change. In a real production workflow, route normal reversions through approval policy.

## Revert command
```powershell
Invoke-RestMethod -Method POST -Uri "https://flags.example/api/v1/projects/acme/environments/production/audit/$auditId/revert" -Headers @{ Authorization = "Bearer $token" }
```

## Verify and follow up
Confirm preview evaluation reasons/values, inspect the new audit record, wait for SSE/poll propagation, and add a removal due date if the failed flag is temporary. Create a post-incident action to remove dead targeting rules.
