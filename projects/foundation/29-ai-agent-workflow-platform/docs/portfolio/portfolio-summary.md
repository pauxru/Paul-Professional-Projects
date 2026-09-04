# AI Agent Workflow Orchestration Platform — Portfolio Summary

**Self-directed engineering case study.**

**Project 29: AI Agent Workflow Orchestration Platform** is an engineering-disciplined, deterministic agent orchestration platform built with C# and ASP.NET Core. Its core thesis is deliberately anti-hype: LLM agents are not good at correctness-critical decision-making, uncontrolled planning, executing arbitrary actions, or being trusted as the source of truth. This platform uses models only where they are useful, and deterministic code wherever correctness matters.

The clearest example is the refund workflow: the model may help gather evidence, but refund eligibility is computed in code, never by the model.

## Engineering thesis

The platform demonstrates a production-minded pattern for LLM-enabled systems:

1. **Deterministic orchestration** — workflow graphs define what can happen, and deterministic steps handle branching, validation, retries, budgets, approvals and terminal outcomes.
2. **Closed tool allow-list** — the model can never execute arbitrary code, shell commands, SQL, filesystem actions or dynamic plugins.
3. **Full replayable traceability** — every model call, tool call, decision and state transition is recorded for audit, debugging and regression testing.

This is not a chatbot wrapper. It is an orchestration engine that treats model output as untrusted input.

## What it does

The project includes three seeded workflows:

| Workflow | Purpose | Important behaviour |
| --- | --- | --- |
| `support-ticket-triage` | Classify, enrich from knowledge base, auto-resolve low-risk tickets or escalate | Prompt-injection attempts are blocked and recorded in the trace |
| `document-summarisation-extraction` | Chunk, summarise, extract structured fields and validate deterministically | Low-confidence output is flagged for review |
| `refund-approval` | Gather evidence, compute eligibility, request approval and execute idempotently | Eligibility is pure deterministic code; approval is required before mutation |

Supported workflow step types are `ModelStep`, `ToolStep`, `ConditionStep`, `ParallelStep`, `LoopStep`, `HumanApprovalStep`, `TransformStep` and `TerminalStep`. Workflow graphs are validated at registration and reject unreachable steps, missing bindings, unknown tools/prompts/transforms and cycles without hard caps.

## Security posture

The security spine is the headline of the project: **model output can never cause arbitrary code, shell, SQL or filesystem execution**.

Tools are closed, statically registered and JSON-schema validated. The registered tool set is intentionally small:

| Tool | Side-effect class | Approval |
| --- | --- | --- |
| `search_knowledge_base` | ReadOnly | No |
| `get_ticket` | ReadOnly | No |
| `lookup_customer` | ReadOnly | No |
| `summarise_document` | ReadOnly / Costly | No |
| `calculate` | ReadOnly | No; safe expression evaluator, no `eval` |
| `update_ticket_status` | Mutating | Risk-based |
| `create_refund_request` | Mutating | Requires approval |
| `send_email` | External | Requires approval |
| `http_get` | External | Allow-listed hosts, SSRF guards, no redirects off-list |

Prompt-injection and confused-deputy scenarios are handled through per-step tool allow-lists, typed parameters, policy checks and trace recording. Unauthorized tool attempts are blocked rather than executed.

## Reliability and operations

The engine supports durable, resumable runs persisted per step, including resume after a simulated crash. It includes retry with backoff for classified transient failures, per-step and per-run timeouts, cancellation, compensation hooks and at-most-once execution of mutating tools via idempotency keys.

Guardrails include per-run and per-tenant caps on tokens, cost, wall-clock time, tool calls and model calls. Loop and oscillation detection halt repeated same-tool/same-argument behaviour with a clear reason. Output-size caps and graceful degradation to human hand-off are built into the workflow model.

Approvals are first-class. Mutating or external actions above a risk threshold pause the run and create an approval task containing the proposed action, arguments and reasoning trace. Operators can approve, reject or modify-and-approve. Approval actions are audited, require the `agents:approve` scope and resume deterministically.

## Observability and evaluation

Every run records a complete replayable trace:

- model prompt version, messages, response, tokens and latency;
- tool arguments, result or error, duration and cost;
- decisions, state transitions and halt reasons;
- an OpenTelemetry span tree mirroring the workflow execution.

A `ReplayModel` allows deterministic replay for regression testing.

The evaluation harness contains **86 seeded scenarios**: 32 triage, 26 summarisation and 28 refund scenarios. It scores task success, tool-selection accuracy, unauthorized-attempt handling, budget adherence, approval correctness, latency and cost.

## Verified numbers

- **Build:** `dotnet build -c Release` succeeds with 0 warnings and 0 errors.
- **Tests:** `dotnet test -c Release` passes **139 total tests**: 129 unit and 10 integration, 0 failed, 0 skipped.
- **Codebase size:** approximately 7,900 lines of C# across 90 files.
- **Evaluation:** 86/86 scenarios pass, 100%.
- **Scores:** task success 1.00, tool-selection accuracy 1.00, unauthorized-handling 1.00, budget-adherence 1.00, approval-correctness 1.00.
- **Blocked unauthorized attempts:** 8.
- **Mean scenario latency:** approximately 36 ms.
- **Regression gate:** passes.

Per-workflow evaluation results:

| Workflow | Result | Mean latency | Notable result |
| --- | ---: | ---: | --- |
| Triage | 32/32 | ~38 ms | 6 unauthorized attempts blocked |
| Summarisation | 26/26 | ~29 ms | 2 unauthorized attempts blocked |
| Refund | 28/28 | ~41 ms | 0 model tokens used for eligibility; eligibility is deterministic code |

Aggregate token and cost totals are dominated by deliberate adversarial cost-exhaustion scenarios that intentionally emit oversized output to prove token-budget guards fire.

## Stack

- .NET 10 / `net10.0`
- ASP.NET Core Minimal APIs
- EF Core 10 with SQLite by default (`agentplatform.db`) and in-memory tests
- JWT bearer authentication using HS256
- OpenTelemetry tracing and metrics
- Serilog structured logging
- xUnit unit and integration tests
- Static web console served at `/`
- API on port **5029**
- Fully offline operation through a deterministic mock model

No Docker or Python is required to build or test the project.

## What this demonstrates

This project demonstrates senior engineering judgment around LLM systems: restraint, explicit boundaries, deterministic control flow, secure tool execution, durable operations, auditability and regression testing. It shows how to integrate LLM capabilities without handing correctness, security or operational control to the model.
