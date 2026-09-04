# Runbook: Approval Backlog

## Summary

Use this runbook when human approval tasks are piling up. Approval pauses are intentional for mutating or external actions above a risk threshold, but a growing backlog can delay workflow completion and leave runs in `WaitingForApproval`.

## Symptoms

- Many pending approvals appear in the approvals inbox.
- Approval latency metric is rising.
- Runs remain in `WaitingForApproval` longer than expected.
- Refund, email or other mutating/external actions are delayed.
- Operators report that approved business actions are not completing because the approval queue has not been processed.

## Diagnosis

1. List approval tasks.

   ```powershell
   Invoke-RestMethod -Method Get -Uri "$base/api/v1/approvals" -Headers $headers
   ```

   ```bash
   curl -s http://localhost:5029/api/v1/approvals \
     -H "Authorization: Bearer $TOKEN"
   ```

2. Check approval age, risk level, workflow and proposed action.

3. Check related runs.

   ```powershell
   Invoke-RestMethod -Method Get -Uri "$base/api/v1/runs/{id}" -Headers $headers
   ```

4. Check metrics.

   ```powershell
   Invoke-RestMethod -Method Get -Uri "$base/api/v1/metrics" -Headers $headers
   ```

5. Confirm whether approvals are timing out to their configured `DefaultOnTimeout` action.

   The default timeout action is usually `Reject`, which resumes the run deterministically according to the workflow's rejection path.

## Common causes

- Not enough approvers for the current volume.
- Risk thresholds are too low, sending too many actions to humans.
- Approval timeout is too long for the operational process.
- Approvers do not have the required `agents:approve` scope.
- A workflow or prompt change increased the number of proposed mutating/external actions.
- External or mutating tools are being used for cases that should be handled by deterministic rules or lower-risk paths.

## Remediation

### Process the inbox

For each pending approval:

1. Review the proposed action, arguments and reasoning trace.
2. Choose one action:

   Approve:

   ```powershell
   Invoke-RestMethod -Method Post -Uri "$base/api/v1/approvals/{approvalId}/approve" -Headers $headers -ContentType 'application/json' -Body '{"comment":"Approved after review."}'
   ```

   Reject:

   ```powershell
   Invoke-RestMethod -Method Post -Uri "$base/api/v1/approvals/{approvalId}/reject" -Headers $headers -ContentType 'application/json' -Body '{"comment":"Rejected after review."}'
   ```

   Modify and approve:

   ```powershell
   Invoke-RestMethod -Method Post -Uri "$base/api/v1/approvals/{approvalId}/modify" -Headers $headers -ContentType 'application/json' -Body '{
     "comment": "Approved with corrected arguments.",
     "arguments": {
       "example": "replace with reviewed tool arguments"
     }
   }'
   ```

3. Confirm the related run resumes deterministically.

   ```powershell
   Invoke-RestMethod -Method Get -Uri "$base/api/v1/runs/{runId}" -Headers $headers
   ```

### Fix authorization

If approvers cannot act:

- verify they have the `agents:approve` scope;
- confirm the token is valid;
- check for ProblemDetails errors from the approval endpoint.

### Reduce unnecessary approvals

If the backlog is caused by too many low-risk actions:

- review risk threshold configuration;
- confirm the workflow is not routing safe read-only operations to approval;
- tune prompts and transforms so they do not over-request mutating or external actions;
- consider deterministic routing for common low-risk cases.

## Escalation

Escalate when:

- approval latency breaches the operational target;
- high-priority runs are blocked behind routine approvals;
- approvals are timing out unexpectedly;
- approvers have correct scope but cannot approve, reject or modify;
- a recent deployment caused a sudden approval volume increase.

Include approval IDs, run IDs, workflow IDs, age of oldest pending approval, approval latency metric and recent workflow or policy changes.

## Prevention

- Alert on approval backlog size and approval latency.
- Staff approvers according to expected workflow volume.
- Tune risk thresholds so humans review meaningful risk, not routine low-risk actions.
- Use sensible approval timeouts and default actions.
- Review approval metrics after workflow, prompt, tool or policy changes.
- Keep mutating and external tools approval-gated where appropriate, but avoid routing deterministic low-risk paths through unnecessary approval.
