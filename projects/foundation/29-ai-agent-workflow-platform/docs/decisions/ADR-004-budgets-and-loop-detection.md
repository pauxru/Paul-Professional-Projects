# ADR-004: Budgets, output caps and loop/oscillation detection as first-class guardrails

## Title
Budgets, output caps and loop/oscillation detection as first-class guardrails

## Status
Accepted

## Context
LLM-agent systems can fail by doing too much: too many model calls, too many tool calls, oversized outputs, repeated actions or waiting too long. These failures can be accidental, caused by malformed prompts, or induced by prompt injection. A deterministic workflow engine still needs runtime guardrails because loops, retries and parallelism are legitimate features when bounded.

The platform supports per-step and per-run timeouts, retries, capped loops, tenant-aware limits and traceable halting reasons. Guardrails are not observability afterthoughts; they determine whether a run may continue.

## Decision
Budgets and output limits are first-class execution controls. The engine enforces per-run and per-tenant caps on tokens, cost, wall-clock duration, tool calls and model calls. It enforces output-size caps before unbounded content can propagate through the workflow. It also detects loop or oscillation patterns, specifically repeated use of the same tool with the same arguments, and halts with a clear reason.

When a run cannot continue safely, the system degrades gracefully to human hand-off rather than continuing to spend budget or attempting speculative recovery.

## Options Considered
| Option | Pros | Cons |
| --- | --- | --- |
| First-class budgets and loop detection | Prevents runaway cost, clearer failure modes, tenant isolation, easier tests | Requires accounting in every model/tool boundary |
| Provider-level rate limits only | Simple external safety net | Too coarse; does not express workflow intent or tenant/run policy |
| Prompt the model not to loop | Easy to add | Not enforceable; fails under prompt injection or model error |
| Manual operator monitoring | Useful backstop | Too late for fast runaway loops and automated CI scenarios |

## Consequences
Positive consequences: cost-exhaustion attacks have a defined ceiling; accidental loops halt deterministically; evaluation scenarios can assert budget adherence; traces include why a run stopped. Tenant caps prevent one workload from consuming shared capacity.

Negative consequences: legitimate long or complex workflows may be halted if configured too tightly. Product owners must choose budgets deliberately rather than relying on defaults forever.

## Risks
Budgets that are too loose become symbolic, while budgets that are too strict create false failures. The mitigation is to record actual token, cost, latency and call counts in traces and evals so limits can be tuned from evidence. Oscillation detection may miss semantically equivalent arguments that are syntactically different; it is a guardrail, not a proof of termination.

## Alternatives Rejected
We rejected unbounded agent loops and retry policies because they are incompatible with deterministic operations. We also rejected relying only on cloud-provider quotas because this project must run fully offline with the deterministic mock model and must enforce workflow-level policy independently.
