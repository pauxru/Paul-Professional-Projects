# Runbook: Stuck Run

## Summary

Use this runbook when a workflow run appears not to be progressing. A run may be legitimately waiting for approval, retrying a transient failure, halted by a guardrail or failed after a timeout. Diagnose from the run status and trace before taking action.

## Symptoms

- A run remains visible in the console without reaching a terminal state.
- The run status is `Running`, `WaitingForApproval`, `Halted` or `Failed` longer than expected.
- No new trace events appear for the run.
- A user reports that an expected ticket update, refund request, email or summary was not completed.
- Approval tasks are pending and related runs are not moving forward.

## Diagnosis

1. Get the run status.

   ```powershell
   $base = 'http://localhost:5029'
   Invoke-RestMethod -Method Get -Uri "$base/api/v1/runs/{id}" -Headers $headers
   ```

   ```bash
   curl -s http://localhost:5029/api/v1/runs/{id} \
     -H "Authorization: Bearer $TOKEN"
   ```

2. Inspect the trace.

   ```powershell
   Invoke-RestMethod -Method Get -Uri "$base/api/v1/runs/{id}/trace" -Headers $headers
   ```

   ```bash
   curl -s http://localhost:5029/api/v1/runs/{id}/trace \
     -H "Authorization: Bearer $TOKEN"
   ```

3. Identify the latest step and state transition.

   Check whether the last trace event is a `HumanApprovalStep` transition, a retry backoff, a timeout failure, a budget halt, a cancellation request, a terminal step or a tool error.

4. Interpret the status.

   | Status | Meaning | Typical action |
   | --- | --- | --- |
   | `WaitingForApproval` | The run is intentionally paused for a human decision | Review the approval task |
   | `Running` | The engine is executing, waiting on a step or retrying | Inspect latest trace event and metrics |
   | `Halted` | A guardrail stopped the run with a halt reason | Inspect `HaltReason` and decide whether to hand off or adjust caps |
   | `Failed` | The run exhausted a timeout or unrecoverable error path | Fix cause, then resume if safe |
   | `Completed` | The run already reached a terminal state | No stuck-run action needed |
   | `Cancelled` | The run was cancelled by request | Confirm cancellation was intended |

## Common causes

- Waiting for human approval for a mutating or external tool call.
- Per-step or per-run timeout exhausted, causing `Failed`.
- Budget halt for tokens, cost, wall-clock, model calls or tool calls.
- Transient-failure retry backoff.
- Loop or oscillation detection.
- External dependency unavailable, slow or rate-limited.

## Remediation

### If the run is waiting for approval

1. List approvals.

   ```powershell
   Invoke-RestMethod -Method Get -Uri "$base/api/v1/approvals" -Headers $headers
   ```

2. Review the proposed action, arguments and reasoning trace.
3. Approve, reject or modify-and-approve.

   ```powershell
   Invoke-RestMethod -Method Post -Uri "$base/api/v1/approvals/{approvalId}/approve" -Headers $headers -ContentType 'application/json' -Body '{"comment":"Reviewed and approved."}'
   ```

   ```powershell
   Invoke-RestMethod -Method Post -Uri "$base/api/v1/approvals/{approvalId}/reject" -Headers $headers -ContentType 'application/json' -Body '{"comment":"Rejected after review."}'
   ```

4. Resume if required.

   ```powershell
   Invoke-RestMethod -Method Post -Uri "$base/api/v1/runs/{id}/resume" -Headers $headers
   ```

### If the run halted on a budget or guardrail

1. Inspect `HaltReason` and the last `BudgetHalt` or guardrail trace event.
2. Decide whether this was expected protection.
3. Hand off to a human if the workflow cannot safely continue.
4. Raise caps only deliberately and only after understanding why the cap was reached.
5. Resume after correcting the cause if the workflow state is safe to continue.

### If the run failed after timeout or unrecoverable error

1. Inspect the failed step in the trace.
2. Fix the underlying cause, such as a dependency outage, tool configuration issue or bad input.
3. Resume the run if the workflow supports safe continuation.
4. Start a new run if the failed run state should not be reused.

### If the run should be stopped

```powershell
Invoke-RestMethod -Method Post -Uri "$base/api/v1/runs/{id}/cancel" -Headers $headers -ContentType 'application/json' -Body '{"reason":"Operator cancelled stuck run after diagnosis."}'
```

## Escalation

Escalate to the platform owner when a run remains `Running` with no new trace events, multiple runs halt unexpectedly, a mutating tool has ambiguous completion, trace data is missing, or an external dependency outage affects many runs.

Include the run ID, workflow ID, tenant, latest trace event, status, halt reason and any approval ID.

## Prevention

- Alert on old `Running` and `WaitingForApproval` runs.
- Alert on approval backlog size and approval latency.
- Set realistic per-step and per-run timeouts.
- Keep budget caps aligned with workflow complexity.
- Monitor budget-halt and tool-error counters.
- Use the evaluation harness and regression gate after changing workflows, prompts, tools or transforms.
