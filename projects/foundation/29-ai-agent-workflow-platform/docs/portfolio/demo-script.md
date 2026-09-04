# Demo Script — AI Agent Workflow Orchestration Platform

This walkthrough is designed for a 5–10 minute portfolio demo. It shows deterministic orchestration, closed tool execution, approval-gated mutations, replayable traces and the offline evaluation harness.

A complete scripted demo also exists at:

```powershell
.\scripts\demo.ps1
```

## 0. Start the API

From the project root:

```powershell
dotnet run --project src\AgentPlatform.Api
```

The API listens on port **5029**.

Open the web console:

```text
http://localhost:5029/
```

Point out:

- the static web console is part of the ASP.NET Core app;
- the project runs without Docker or Python;
- the deterministic mock model allows the demo and tests to run offline.

## 1. Mint a development JWT

The development token endpoint is available in development mode.

### PowerShell

```powershell
$base = 'http://localhost:5029'
$tokenResponse = Invoke-RestMethod -Method Post -Uri "$base/api/v1/dev/token" -ContentType 'application/json' -Body '{"tenantId":"demo-tenant","subject":"demo-presenter","scopes":["agents:run","agents:approve","agents:admin"]}'
$token = $tokenResponse.accessToken
$headers = @{ Authorization = "Bearer $token" }
```

### curl

```bash
TOKEN=$(curl -s -X POST http://localhost:5029/api/v1/dev/token \
  -H "Content-Type: application/json" \
  -d '{"tenantId":"demo-tenant","subject":"demo-presenter","scopes":["agents:run","agents:approve","agents:admin"]}' | jq -r .accessToken)
```

Point out:

- production routes use JWT bearer auth;
- route policies use `agents:run`, `agents:approve` and `agents:admin` scopes.

## 2. Show registered workflows and tools

### PowerShell

```powershell
Invoke-RestMethod -Method Get -Uri "$base/api/v1/workflows" -Headers $headers
Invoke-RestMethod -Method Get -Uri "$base/api/v1/tools" -Headers $headers
```

### curl

```bash
curl -s http://localhost:5029/api/v1/workflows -H "Authorization: Bearer $TOKEN"
curl -s http://localhost:5029/api/v1/tools -H "Authorization: Bearer $TOKEN"
```

Point out:

- workflows are registered graphs, not open-ended model plans;
- tools are statically registered and closed over a known allow-list;
- there is no shell tool, no dynamic plugin loading and no arbitrary HTTP tool.

## 3. Run support-ticket triage: auto-resolve a low-risk ticket

Start a triage run for `TCK-1001`.

### PowerShell

```powershell
$triageLowRisk = Invoke-RestMethod -Method Post -Uri "$base/api/v1/runs" -Headers $headers -ContentType 'application/json' -Body '{
  "workflowId": "support-ticket-triage",
  "input": {
    "ticketId": "TCK-1001"
  }
}'
$triageLowRisk.id
Invoke-RestMethod -Method Get -Uri "$base/api/v1/runs/$($triageLowRisk.id)" -Headers $headers
Invoke-RestMethod -Method Get -Uri "$base/api/v1/runs/$($triageLowRisk.id)/trace" -Headers $headers
```

### curl

```bash
TRIAGE_LOW_RISK_RUN_ID=$(curl -s -X POST http://localhost:5029/api/v1/runs \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"workflowId":"support-ticket-triage","input":{"ticketId":"TCK-1001"}}' | jq -r .id)

curl -s http://localhost:5029/api/v1/runs/$TRIAGE_LOW_RISK_RUN_ID \
  -H "Authorization: Bearer $TOKEN"

curl -s http://localhost:5029/api/v1/runs/$TRIAGE_LOW_RISK_RUN_ID/trace \
  -H "Authorization: Bearer $TOKEN"
```

Point out:

- the workflow classifies the ticket;
- it enriches from the knowledge base;
- low-risk cases can be auto-resolved by a permitted mutating tool;
- the trace records the model call, tool call, decision and terminal state.

## 4. Run support-ticket triage: escalate an angry ticket

Use a ticket fixture representing an angry or higher-risk support case.

### PowerShell

```powershell
$triageAngry = Invoke-RestMethod -Method Post -Uri "$base/api/v1/runs" -Headers $headers -ContentType 'application/json' -Body '{
  "workflowId": "support-ticket-triage",
  "input": {
    "ticketId": "TCK-ANGRY-1"
  }
}'
Invoke-RestMethod -Method Get -Uri "$base/api/v1/runs/$($triageAngry.id)/trace" -Headers $headers
```

### curl

```bash
TRIAGE_ANGRY_RUN_ID=$(curl -s -X POST http://localhost:5029/api/v1/runs \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"workflowId":"support-ticket-triage","input":{"ticketId":"TCK-ANGRY-1"}}' | jq -r .id)

curl -s http://localhost:5029/api/v1/runs/$TRIAGE_ANGRY_RUN_ID/trace \
  -H "Authorization: Bearer $TOKEN"
```

Point out:

- deterministic conditions decide whether the workflow auto-resolves or escalates;
- escalation is graceful degradation, not a failed agent.

## 5. Show prompt-injection handling with `TCK-INJ-1`

Start a triage run against the prompt-injection fixture.

### PowerShell

```powershell
$injectionRun = Invoke-RestMethod -Method Post -Uri "$base/api/v1/runs" -Headers $headers -ContentType 'application/json' -Body '{
  "workflowId": "support-ticket-triage",
  "input": {
    "ticketId": "TCK-INJ-1"
  }
}'
Invoke-RestMethod -Method Get -Uri "$base/api/v1/runs/$($injectionRun.id)/trace" -Headers $headers
```

### curl

```bash
INJECTION_RUN_ID=$(curl -s -X POST http://localhost:5029/api/v1/runs \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"workflowId":"support-ticket-triage","input":{"ticketId":"TCK-INJ-1"}}' | jq -r .id)

curl -s http://localhost:5029/api/v1/runs/$INJECTION_RUN_ID/trace \
  -H "Authorization: Bearer $TOKEN"
```

Point out:

- retrieved content is treated as untrusted;
- the model cannot request tools outside the current step allow-list;
- the unauthorized attempt is blocked and recorded in the trace;
- no arbitrary shell, SQL, filesystem or HTTP execution path exists.

## 6. Run document summarisation and extraction

### PowerShell

```powershell
$summaryRun = Invoke-RestMethod -Method Post -Uri "$base/api/v1/runs" -Headers $headers -ContentType 'application/json' -Body '{
  "workflowId": "document-summarisation-extraction",
  "input": {
    "documentId": "DOC-1001"
  }
}'
Invoke-RestMethod -Method Get -Uri "$base/api/v1/runs/$($summaryRun.id)/trace" -Headers $headers
```

### curl

```bash
SUMMARY_RUN_ID=$(curl -s -X POST http://localhost:5029/api/v1/runs \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"workflowId":"document-summarisation-extraction","input":{"documentId":"DOC-1001"}}' | jq -r .id)

curl -s http://localhost:5029/api/v1/runs/$SUMMARY_RUN_ID/trace \
  -H "Authorization: Bearer $TOKEN"
```

Point out:

- the workflow chunks, summarises and extracts structured fields;
- deterministic validation checks the output;
- low-confidence results are routed for review instead of being silently trusted.

## 7. Run refund approval and approve it

Start the refund workflow.

### PowerShell

```powershell
$refundRun = Invoke-RestMethod -Method Post -Uri "$base/api/v1/runs" -Headers $headers -ContentType 'application/json' -Body '{
  "workflowId": "refund-approval",
  "input": {
    "ticketId": "TCK-REFUND-1",
    "orderId": "ORD-1001"
  }
}'
Invoke-RestMethod -Method Get -Uri "$base/api/v1/runs/$($refundRun.id)" -Headers $headers
```

### curl

```bash
REFUND_RUN_ID=$(curl -s -X POST http://localhost:5029/api/v1/runs \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"workflowId":"refund-approval","input":{"ticketId":"TCK-REFUND-1","orderId":"ORD-1001"}}' | jq -r .id)

curl -s http://localhost:5029/api/v1/runs/$REFUND_RUN_ID \
  -H "Authorization: Bearer $TOKEN"
```

The run should pause for approval when it reaches the mutating refund action.

List approvals and approve:

```powershell
$approvals = Invoke-RestMethod -Method Get -Uri "$base/api/v1/approvals" -Headers $headers
$approvalId = $approvals.items[0].id
Invoke-RestMethod -Method Post -Uri "$base/api/v1/approvals/$approvalId/approve" -Headers $headers -ContentType 'application/json' -Body '{
  "comment": "Approved during portfolio demo after reviewing proposed action and trace."
}'
Invoke-RestMethod -Method Post -Uri "$base/api/v1/runs/$($refundRun.id)/resume" -Headers $headers
Invoke-RestMethod -Method Get -Uri "$base/api/v1/runs/$($refundRun.id)/trace" -Headers $headers
```

curl equivalent:

```bash
APPROVAL_ID=$(curl -s http://localhost:5029/api/v1/approvals \
  -H "Authorization: Bearer $TOKEN" | jq -r '.items[0].id')

curl -s -X POST http://localhost:5029/api/v1/approvals/$APPROVAL_ID/approve \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"comment":"Approved during portfolio demo after reviewing proposed action and trace."}'

curl -s -X POST http://localhost:5029/api/v1/runs/$REFUND_RUN_ID/resume \
  -H "Authorization: Bearer $TOKEN"
```

Point out:

- refund eligibility is deterministic code, not model output;
- the approval task carries the proposed action, arguments and reasoning trace;
- the final mutation is idempotent and audit-recorded;
- the `agents:approve` scope is required.

## 8. Use the web console

In the browser, show:

1. run list;
2. trace timeline;
3. approvals inbox;
4. tool registry;
5. evaluation results.

Point out that the trace timeline is the main artifact: it lets reviewers see the exact chain of model calls, tool calls, decisions and state transitions.

## 9. Run the evaluation suite and compare the regression gate

### PowerShell

```powershell
$evalRun = Invoke-RestMethod -Method Post -Uri "$base/api/v1/evals/run" -Headers $headers -ContentType 'application/json' -Body '{
  "suite": "seeded"
}'
$evalRun
Invoke-RestMethod -Method Post -Uri "$base/api/v1/evals/compare" -Headers $headers -ContentType 'application/json' -Body '{}'
```

### curl

```bash
curl -s -X POST http://localhost:5029/api/v1/evals/run \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"suite":"seeded"}'

curl -s -X POST http://localhost:5029/api/v1/evals/compare \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{}'
```

Point out the real measured offline deterministic results:

- 86/86 scenarios pass;
- task success 1.00;
- tool-selection accuracy 1.00;
- unauthorized-handling 1.00;
- budget-adherence 1.00;
- approval-correctness 1.00;
- 8 unauthorized attempts blocked;
- mean scenario latency approximately 36 ms;
- regression gate passes.

Close with the project thesis: this is how to build LLM agent systems when correctness, security and auditability matter.
