# Agent Engineering Principles: When to use an agent, when deterministic code wins, and how to bound blast radius

The AI Agent Workflow Orchestration Platform is intentionally anti-hype. It is not a demonstration that a model can be persuaded to act like an application server. It is an engineering exercise in putting language models where they are useful while keeping authority, safety and correctness in deterministic software.

The central rule is simple:

```text
The model proposes; deterministic code disposes.
```

A model can help interpret ambiguous language, draft text, classify intent, extract fields from messy content or summarise a conversation. It should not own eligibility, money movement, routing, validation, authorization, retry policy or state transitions. Those decisions have invariants. They need tests, audits and deterministic failure modes. When a decision has a correct answer, encode it in code.

## 1. Use agents for fuzzy language work, not for authoritative decisions

LLMs are useful where the input is human, irregular and semantically rich. In this project, that means bounded `ModelStep` work such as classification, summarisation, extraction and drafting. The workflow may ask the model to transform natural language into structured candidate data, but that candidate data is still validated by typed schemas and deterministic rules before it affects execution.

Deterministic code owns decisions with business or safety consequences. Refund eligibility is the clearest example: the refund workflow uses `RefundEligibilityCalculator` in the Domain layer and consumes zero model tokens for eligibility. That is not a limitation; it is the point. Eligibility is a policy decision with a correct answer for the provided inputs. Asking a model to decide it would make the system less reliable, less testable and harder to explain.

The platform's workflow graph makes this split visible. `ModelStep` can handle language. `ConditionStep` branches using deterministic predicates. `ToolStep` invokes a bounded capability. `HumanApprovalStep` gates risky actions. `LoopStep` is capped. `TerminalStep` ends explicitly. The model does not choose the next step and does not decide when the run is finished.

## 2. Never let model output reach an interpreter

The most important security property is not better prompt wording. It is that model output can never become arbitrary execution.

This project has no `eval`, no dynamic assembly loading, no shell tool, no arbitrary SQL execution, no filesystem execution path and no general-purpose HTTP client exposed to the model. Model responses are data. They may contain proposed tool names and arguments in a function-calling shape, but those proposals are checked against a closed, statically registered allow-list.

If a tool is not compiled into the platform and registered with a schema, it does not exist. If the current step has not allow-listed that tool, the call is blocked and recorded as a policy violation in the trace. If arguments are malformed, missing or extra, the JSON-schema validator returns a structured error. Invalid arguments do not become exceptions that accidentally skip policy, and they do not partially execute.

`calculate` illustrates the principle at small scale. It is a safe expression evaluator, not a way to run C#, JavaScript or a shell. `http_get` illustrates it at integration scale. It can reach only allow-listed hosts, blocks private, loopback and link-local ranges, and does not permit redirects to off-list destinations. SSRF prevention is enforced by code, not by telling the model to be careful.

## 3. Bound blast radius at every boundary

Agent systems fail in distinctive ways: a prompt injection requests an unauthorized action; a model hallucinates a tool; a loop repeats the same call; a provider returns oversized output; a transient failure retries into a duplicate mutation; an external action needs human judgment. The platform treats these as expected engineering cases.

The blast-radius controls are layered:

| Boundary | Control in this project |
| --- | --- |
| Tool discovery | Closed, statically registered allow-list |
| Tool shape | Typed JSON-schema validation and coercion |
| Step authority | Per-step tool allow-lists |
| Identity/approval | `agents:approve` scope for approval decisions |
| Network access | `http_get` host allow-list and SSRF guards |
| Execution volume | Per-run and per-tenant budgets for tokens, cost, wall-clock, tool calls and model calls |
| Output growth | Output-size caps |
| Runaway behaviour | Capped loops and repeated tool+arguments oscillation detection |
| Mutations | Idempotency keys for at-most-once execution |
| Risky external actions | Human approval at the action boundary |

Budgets matter because cost exhaustion is a real failure mode even when no data is corrupted. The engine enforces caps per run and per tenant, and it records the reason when a run is halted. Loop detection matters because a model that repeatedly asks for the same tool with the same arguments is no longer making progress. The correct response is not infinite patience; it is a clear stop reason and graceful degradation to human hand-off.

Human approval belongs at the action boundary. Approving a workflow at the beginning does not protect against malicious retrieved content or a later confused-deputy attempt. Approving after execution is only audit. The approval task must show the proposed action, typed arguments and reasoning trace, then allow approve, reject, or modify-and-approve. After approval, execution still uses idempotency keys so retry does not double-apply a mutation.

## 4. Observability is a correctness tool

Logs are not enough. This platform records a complete replayable trace for every run: model calls with prompt version, messages, response, tokens and latency; tool calls with arguments, result, error, duration and cost; decisions; policy violations; and state transitions. The OpenTelemetry span tree mirrors that structure so operational telemetry and deterministic traces tell the same story.

This is not only for debugging. It is how the system is tested. `ReplayModel` can re-run a recorded workflow against recorded model responses, making behaviour deterministic in CI. The evaluation harness runs 86 seeded scenarios across the three workflows and scores task success, tool-selection accuracy, unauthorized-attempt handling, budget adherence, approval correctness and latency/cost. The current project passes 86/86, all scored metrics are 1.00, the regression gate passes, mean scenario latency is about 36ms, and 8 unauthorized attempts are blocked.

Those numbers should be treated honestly: they describe this project's seeded eval set, not universal real-world performance. Their value is that regressions become visible. If a prompt change causes a tool-selection error, an approval miss, a budget breach or a policy bypass, the gate can fail offline before a live provider is involved.

Prompt management supports the same discipline. Templates are versioned. Rendering is strict: missing or extra variables are rejected. Each run records the exact prompt version used. Prompt A/B in evals is possible because prompts are assets with versions, not anonymous strings scattered through application code.

## 5. Prompt injection is normal input, not an exotic attack

Any text the model sees is untrusted. That includes user messages, retrieved content, tool output and prior summaries. The platform assumes some of that content may contain instructions such as "ignore previous rules" or requests to call unauthorized tools. The defense is architectural.

Prompt wording can reduce accidental mistakes, but it is not a boundary. The boundary is that unauthorized tools are not available, authorized tools require typed parameters, risky actions pause for approval, budgets stop runaway behaviour, and deterministic code owns control flow. A prompt injection can ask for a shell command; there is no shell tool. It can ask for arbitrary HTTP; `http_get` is allow-listed and SSRF-guarded. It can ask for a refund to be approved; refund eligibility is Domain code and approval requires the right step and scope.

This is why blocked attempts are recorded in the trace. A blocked unauthorized attempt is not only a non-event; it is evidence that the system encountered hostile or erroneous input and enforced policy.

## 6. Honest non-claims

This project does not claim that agents are generally autonomous employees. It does not claim that prompts can make arbitrary execution safe. It does not claim live OpenAI or Azure OpenAI providers are required for correctness; the default path is a deterministic mock model, and provider adapters sit behind configuration and environment variables. It does not claim the 86 seeded scenarios represent all possible production traffic. It does not claim SQLite is the only persistence choice; it is the default implementation used here with EF Core.

It also does not remove the need for product judgment. Budgets must be set. Approval thresholds must reflect real risk. Tool schemas must be maintained. New workflows must decide which parts are fuzzy language tasks and which parts are deterministic policy.

The engineering thesis is narrower and stronger: a useful agent platform can be built by treating the model as an untrusted language component inside a deterministic application. The model may propose structured work. The workflow engine validates it, budgets it, records it, replays it and, when necessary, asks a human before crossing an action boundary. That is less magical than model-driven planning, and it is much easier to operate.
