# Portfolio Screenshots and Recordings Needed

Capture these assets for the portfolio page. Use real local demo output; if any demo input is fictional, label it as fictional demo data.

- [ ] **Web console run list** — show several completed and waiting runs across triage, summarisation and refund workflows; demonstrates that orchestration is visible and operator-friendly.
- [ ] **Trace timeline viewer** — show model spans, tool spans, decisions and state transitions in one run; demonstrates replayable auditability rather than opaque agent behaviour.
- [ ] **Approvals inbox with pending refund** — show a refund approval task with proposed action, arguments and reasoning trace; demonstrates human control over mutating/external actions.
- [ ] **Tool registry page** — show the nine statically registered tools with side-effect class and approval requirements; demonstrates the closed allow-list security model.
- [ ] **Evaluation results page** — show the seeded evaluation suite and regression gate passing; demonstrates that behaviour is measured, not hand-waved.
- [ ] **Blocked prompt-injection trace** — show `TCK-INJ-1` producing an unauthorized tool attempt that is blocked and recorded; demonstrates prompt-injection handling and confused-deputy protection.
- [ ] **Terminal showing `dotnet test -c Release` all green** — show 139 tests passing, 0 failed and 0 skipped; demonstrates verified engineering quality.
- [ ] **Terminal showing `dotnet build -c Release`** — show build success with 0 warnings and 0 errors; demonstrates clean Release build health.
- [ ] **OpenAPI page at `/openapi`** — show documented routes for workflows, runs, trace, approvals, tools, prompts, evals and metrics; demonstrates a usable API surface.
- [ ] **Refund trace detail** — show refund eligibility performed without model-token usage for the eligibility decision; demonstrates deterministic correctness for a high-risk decision.
- [ ] **Budget-halt or adversarial eval trace** — show an oversized-output or budget-exhaustion scenario halting safely; demonstrates guardrails under adversarial pressure.
- [ ] **Short screen recording of `scripts/demo.ps1`** — show all three workflows including approval in one scripted path; provides a compact walkthrough for reviewers.
