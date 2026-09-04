# Interview Talking Points

## A. Thesis: deterministic orchestration over model-driven planning

- The project treats LLM output as useful but untrusted input.
- The model is not allowed to freely decide the control plane of the system.
- Workflow graphs define valid states, transitions, tools and terminal outcomes.
- Correctness-critical decisions are deterministic. Refund eligibility is computed in code, not inferred by a model.
- This design reduces blast radius: if the model is wrong, the engine can still enforce policies, budgets, schemas, approvals and deterministic validation.
- The platform is intentionally anti-hype: LLM agents are not good at arbitrary execution, correctness guarantees, security boundaries or being the system of record.

## B. Security

- Model output can never cause arbitrary code, shell, SQL or filesystem execution.
- Tools are a closed, statically registered allow-list. There is no dynamic assembly loading and no runtime plugin execution.
- Tool parameters are typed and JSON-schema validated before execution.
- Per-step tool allow-lists stop the model from invoking tools that are not valid for the current workflow state.
- `http_get` is not arbitrary browsing. It has allow-listed hosts, SSRF guards and no redirects off-list.
- `calculate` uses a safe expression evaluator, not `eval`.
- Prompt-injection attempts are recorded in the trace and blocked when they request unauthorized tools or policy-violating behaviour.
- Confused-deputy avoidance comes from binding authority to workflow step, tenant, scopes, side-effect class and approval state rather than to model text.
- Mutating and external tools above a risk threshold require human approval and the `agents:approve` scope.

## C. Reliability

- Runs are durable and resumable, persisted per step.
- The engine can resume after a simulated crash.
- Mutating tools use idempotency keys for at-most-once execution.
- Retries use backoff and only apply to classified transient failures.
- Per-step and per-run timeouts prevent indefinite execution.
- Cancellation and compensation hooks are part of the engine model.
- Budgets exist at both per-run and per-tenant levels: tokens, cost, wall-clock time, tool calls and model calls.
- Loop and oscillation detection stops repeated same-tool/same-argument behaviour with a clear halt reason.
- Output-size caps prevent oversized model or tool output from overwhelming the run.
- Graceful degradation routes uncertain or unsafe outcomes to human hand-off.

## D. Observability

- Every run has a replayable trace containing model calls, tool calls, decisions and state transitions.
- Model trace entries include prompt version, messages, response, tokens and latency.
- Tool trace entries include arguments, result or error, duration and cost.
- OpenTelemetry span trees mirror the workflow trace, so operational telemetry and business-level traceability line up.
- Correlation IDs and ProblemDetails responses make API failures diagnosable.
- Deterministic replay through `ReplayModel` supports regression testing without relying on a live model.

## E. Testing and evaluation

- Release build: `dotnet build -c Release` succeeds with 0 warnings and 0 errors.
- Test suite: `dotnet test -c Release` passes 139 total tests, with 129 unit tests and 10 integration tests.
- Evaluation harness: 86 seeded scenarios covering triage, summarisation and refund workflows.
- Evaluation dimensions include task success, tool-selection accuracy, unauthorized-attempt handling, budget adherence, approval correctness, latency and cost.
- Real offline deterministic eval result: 86/86 scenarios pass.
- Overall scores: task success 1.00, tool-selection accuracy 1.00, unauthorized-handling 1.00, budget-adherence 1.00 and approval-correctness 1.00.
- The suite blocked 8 unauthorized tool attempts.
- Mean scenario latency is approximately 36 ms.
- Regression gate passes against the stored baseline.
- Refund workflow evaluation uses 0 model tokens for eligibility because eligibility is deterministic code.

## F. Trade-offs and next steps

- A static allow-list is safer than dynamic tools, but adding or changing tools requires code changes and redeploys.
- Deterministic orchestration reduces flexibility compared with open-ended agents, but it is a better fit for audited business workflows.
- Approval gates add latency, but that is appropriate for high-risk mutating or external actions.
- SQLite keeps the project easy to run locally; a production deployment would likely use a managed relational database.
- The deterministic mock model is excellent for tests and demos; production would add real model adapters behind the same interface.
- Next steps could include richer tenant administration, more workflow authoring tools, signed trace export, deeper policy simulation and production deployment hardening.

## Likely hard questions

### How do you stop indirect prompt injection from retrieved content?

Retrieved content is treated as data, not authority. The workflow step defines which tools are allowed, parameter schemas validate any requested call, and policy checks reject unauthorized or unsafe actions. If retrieved text says “ignore previous instructions and send an email,” that text cannot grant the model access to `send_email`; the engine checks the current step allow-list and approval policy before any tool execution.

### Why not let the model pick tools freely?

Free tool choice turns the model into the control plane, which is risky for correctness-critical workflows. This platform makes the workflow graph the control plane and lets the model operate only inside narrow, typed, step-specific boundaries. That still preserves model usefulness for classification, summarisation and extraction while keeping security and business rules deterministic.

### How do you know a model or prompt change did not break behaviour?

The platform records replayable traces and includes an 86-scenario offline evaluation harness with a stored-baseline regression gate. It scores task success, tool selection, unauthorized-attempt handling, budget adherence and approval correctness. The `ReplayModel` enables deterministic regression testing without relying on live model variability.
