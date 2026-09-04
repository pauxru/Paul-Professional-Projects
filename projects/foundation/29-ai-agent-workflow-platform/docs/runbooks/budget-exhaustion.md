# Runbook: Budget Exhaustion

## Summary

Use this runbook when a workflow run halts because it exceeded a configured budget. The platform enforces per-run and per-tenant caps on tokens, cost, wall-clock time, tool calls and model calls. Budget halts are intentional guardrails, not infrastructure crashes.

## Symptoms

- Run status is `Halted`.
- Run outcome is `Escalated`.
- `HaltReason` is budget-related, such as `TokenBudgetExceeded`, `ToolCallBudgetExceeded`, `ModelCallBudgetExceeded`, `CostBudgetExceeded` or `WallClockExceeded`.
- The trace contains a `BudgetHalt` event.
- Budget-halt metric counters increase.
- The console shows graceful hand-off rather than successful completion.

## Diagnosis

1. Inspect the run.

   ```powershell
   Invoke-RestMethod -Method Get -Uri "$base/api/v1/runs/{id}" -Headers $headers
   ```

2. Inspect the trace.

   ```powershell
   Invoke-RestMethod -Method Get -Uri "$base/api/v1/runs/{id}/trace" -Headers $headers
   ```

3. Find the first `BudgetHalt` trace event and identify:

   - which cap was exceeded;
   - current budget consumption;
   - configured cap;
   - last model call or tool call before the halt;
   - whether a loop or repeated same-tool/same-argument pattern occurred.

4. Check metrics.

   ```powershell
   Invoke-RestMethod -Method Get -Uri "$base/api/v1/metrics" -Headers $headers
   ```

5. Determine whether the halt is expected.

   The evaluation harness includes adversarial cost-exhaustion scenarios that deliberately emit oversized output to prove token-budget guards fire. Those budget halts are expected in that context.

## Remediation

### Safe expected halt

If the halt protected the system from runaway model output, excessive tool calls or wall-clock overrun:

1. Leave the run halted.
2. Hand off to a human reviewer if the business process still needs resolution.
3. Record the halt reason and trace link in the operational ticket.

### Cap too low for a legitimate workflow

If the trace shows valid progress and no loop, oscillation or oversized output:

1. Raise the relevant cap deliberately, either per-run or per-tenant according to operating policy.
2. Document why the higher cap is required.
3. Resume the run only after confirming the workflow state is safe.

```powershell
Invoke-RestMethod -Method Post -Uri "$base/api/v1/runs/{id}/resume" -Headers $headers
```

### Possible loop or oscillation

If budget use is driven by repeated same-tool/same-argument calls:

1. Treat the halt as correct.
2. Inspect the workflow step, transform and prompt that produced the repeated action.
3. Tighten the loop cap, step condition or tool allow-list as needed.
4. Add or update an evaluation scenario before redeploying.

### Excessive output

If a model or tool produced oversized output:

1. Confirm output-size caps fired.
2. Reduce requested output size, chunk input more aggressively or tighten schema expectations.
3. Add regression coverage for the oversized-output case.

## Escalation

Escalate when:

- many tenants hit the same budget halt unexpectedly;
- a workflow that previously passed the regression gate now halts;
- cost or token usage jumps after a prompt, model adapter, workflow or tool change;
- budget metrics disagree with trace entries;
- a halted run involves a high-priority business case that requires manual handling.

Include run ID, tenant, workflow ID, `HaltReason`, cap values, observed consumption, latest trace event and recent deployment changes.

## Prevention

- Right-size budgets by workflow type and tenant risk.
- Watch budget-halt counters and alert on unusual increases.
- Keep loop caps and oscillation detection enabled.
- Use output-size caps for model and tool responses.
- Include budget-adherence checks in the evaluation harness.
- Review adversarial scenarios before changing prompts, transforms or model adapters.
- Prefer deterministic code for bounded decisions rather than spending model calls on logic the system can compute directly.
