# Test Results

Real output from this repository. Reproduce with:

```powershell
dotnet build AgentPlatform.slnx -c Release
dotnet test  AgentPlatform.slnx -c Release
```

Environment: Windows, .NET SDK 10.0.400, `net10.0`. Fully offline — the default
`DeterministicMockModel` is used, no network calls, no API keys, EF Core + SQLite.

---

## Build — `dotnet build -c Release`

```
  AgentPlatform.Domain -> ...\src\AgentPlatform.Domain\bin\Release\net10.0\AgentPlatform.Domain.dll
  AgentPlatform.Application -> ...\src\AgentPlatform.Application\bin\Release\net10.0\AgentPlatform.Application.dll
  AgentPlatform.Infrastructure -> ...\src\AgentPlatform.Infrastructure\bin\Release\net10.0\AgentPlatform.Infrastructure.dll
  AgentPlatform.UnitTests -> ...\tests\AgentPlatform.UnitTests\bin\Release\net10.0\AgentPlatform.UnitTests.dll
  AgentPlatform.Api -> ...\src\AgentPlatform.Api\bin\Release\net10.0\AgentPlatform.Api.dll
  AgentPlatform.IntegrationTests -> ...\tests\AgentPlatform.IntegrationTests\bin\Release\net10.0\AgentPlatform.IntegrationTests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:02.39
```

**Result: build succeeded, 0 warnings, 0 errors** across all 6 projects.

---

## Test — `dotnet test -c Release`

```
Passed!  - Failed:     0, Passed:    10, Skipped:     0, Total:    10, Duration: 2 s - AgentPlatform.IntegrationTests.dll (net10.0)

Passed!  - Failed:     0, Passed:   129, Skipped:     0, Total:   129, Duration: 6 s - AgentPlatform.UnitTests.dll (net10.0)
```

| Project | Passed | Failed | Skipped | Total |
|---|---:|---:|---:|---:|
| AgentPlatform.UnitTests | 129 | 0 | 0 | 129 |
| AgentPlatform.IntegrationTests | 10 | 0 | 0 | 10 |
| **Total** | **139** | **0** | **0** | **139** |

> The 129 unit results come from 99 `[Fact]`/`[Theory]` methods; `[Theory]` cases
> expand into multiple executed test cases (e.g. the arithmetic and rule tables).

---

## What the tests cover

### Unit tests (`tests/AgentPlatform.UnitTests`)

| Test file | Tests | Focus |
|---|---:|---|
| `Security/SchemaValidationTests.cs` | 9 | JSON-schema accept / reject / safe coercion; no mutation of the source node |
| `Security/SafeExpressionEvaluatorTests.cs` | 6 | `calculate` arithmetic; rejects identifiers, reflection-looking input, div-by-zero, over-long input — cannot escape |
| `Security/UrlSecurityPolicyTests.cs` | 6 | `http_get` allow-list, DNS-rebinding, private/loopback/link-local classification |
| `Models/MockModelTests.cs` | 11 | Every adversarial behaviour (malformed JSON, hallucinated tool, unauthorised tool, loop, refusal, timeout, oversized) + triage classification |
| `Prompts/PromptTemplateTests.cs` | 7 | Strict rendering; missing / extra variable rejection; version key |
| `Rules/RefundEligibilityTests.cs` | 10 | Deterministic refund eligibility rules (windows, defect, fraud guard, high-value) |
| `Budgets/LoopDetectorTests.cs` | 4 | Oscillation detection (same tool + same arguments) |
| `Engine/ConditionEvaluatorTests.cs` | 6 | Deterministic branching operators |
| `Workflows/WorkflowGraphValidatorTests.cs` | 9 | Rejects unreachable steps, cycles without caps, missing bindings, unknown tools/prompts/transforms |
| `Tools/ToolInvokerTests.cs` | 8 | Unauthorised / unknown / malformed / schema-reject / coercion / approval-gate / **idempotent no-double-execute** / output cap |
| `Engine/WorkflowExecutionTests.cs` | 19 | All 3 workflows end-to-end; injection blocked; unauthorised blocked; loop & budget halts; per-step & per-run timeouts; **resume after simulated crash**; approval approve/reject/modify; **deterministic replay** |
| `Evaluation/EvaluationHarnessTests.cs` | 4 | Suite scoring; regression gate pass and fail paths |

### Integration tests (`tests/AgentPlatform.IntegrationTests`)

`WebApplicationFactory<Program>` over an in-memory SQLite connection, `Testing` environment:

- Starting a run without a token → **401**.
- Listing approvals without the `agents:approve` scope → **403**.
- `/api/v1/tools` returns the closed 9-tool allow-list.
- `/api/v1/workflows` lists the three seeded workflows.
- Triage run started → fetched → traced (trace has events).
- Missing `workflowName` → **400** `application/problem+json`.
- Unknown run id → **404**.
- Dev-token endpoint mints a bearer token.
- **Full refund approval flow over HTTP**: start → pause → list approvals → approve → run `Completed`/`Succeeded`.
- `/health` is anonymous and returns 200.

---

## Notes / honesty

- No test reaches the network; the OpenAI/Azure adapters are exercised only with a stubbed
  `HttpMessageHandler` and are never used by default.
- Two real defects were found and fixed while writing the suite: (1) SQLite cannot `ORDER BY`
  a `DateTimeOffset`, which broke the approvals inbox and run list on the default provider —
  fixed with a global value converter storing UTC ticks; (2) the engine resumed approvals by
  re-querying for a *pending* task after the decision had already moved it out of pending,
  so every decision routed to the reject branch — fixed by fetching the latest decided approval.
- Docker images and the compose stack are **not** verified (no Docker on the build host).
