# Upwork Portfolio Description

## AI Agent Workflow Orchestration Platform

I built this self-directed engineering case study to demonstrate how I approach production-disciplined LLM agent systems in .NET: deterministic orchestration, strict tool boundaries, human approvals, budgets, full audit traces and offline regression evaluation.

The project is intentionally anti-hype. It starts from the assumption that model output is untrusted and that correctness-critical decisions should be handled by deterministic code. For example, the refund workflow can use model-assisted evidence gathering, but refund eligibility is computed in C# code, never by the model.

### What the platform includes

- ASP.NET Core Minimal API for workflow execution, run management, approvals, traces, tools, prompts, evaluations and metrics.
- Durable workflow engine with model steps, tool steps, deterministic conditions, parallel steps, capped loops, human approvals, transforms and terminal states.
- Closed, statically registered tool allow-list with typed JSON-schema validated parameters.
- Security guardrails preventing arbitrary shell, SQL, filesystem, dynamic assembly loading or unrestricted HTTP execution.
- Approval-gated mutating and external actions, including approve, reject and modify-and-approve flows.
- Per-run and per-tenant budgets for tokens, cost, wall-clock time, tool calls and model calls.
- Full replayable execution trace for every model call, tool call, decision and state transition.
- OpenTelemetry tracing and metrics, Serilog logging and ProblemDetails API errors.
- Offline deterministic model adapter for local demos and repeatable tests.
- Evaluation harness with 86 seeded scenarios and a stored-baseline regression gate.

### Verified project results

The Release build succeeds with 0 warnings and 0 errors. The test suite passes 139 tests: 129 unit tests and 10 integration tests, with 0 failed and 0 skipped. The offline deterministic evaluation suite passes 86/86 seeded scenarios, including blocked prompt-injection attempts and approval correctness checks.

### Technologies

C#, .NET 10, ASP.NET Core Minimal APIs, EF Core 10, SQLite, JWT bearer authentication, OpenTelemetry, Serilog, xUnit, static HTML/JavaScript console.

If you need similar work — agent platforms, LLM integration with guardrails, deterministic workflow engines, human-in-the-loop approvals or secure .NET backend systems — I can help design and build it with production engineering discipline.
