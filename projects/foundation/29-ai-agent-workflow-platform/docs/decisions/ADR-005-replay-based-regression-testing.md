# ADR-005: Replay-based regression testing with recorded model responses

## Title
Replay-based regression testing with recorded model responses

## Status
Accepted

## Context
Testing agent behaviour against live models is expensive, slow and non-deterministic. Snapshot-only tests catch formatting changes but often miss behavioural regressions: the wrong tool selected, a policy violation not blocked, a budget exceeded, or a branch taken for the wrong reason. This platform is designed to be fully offline by default and to make agent behaviour regression-testable.

Every run records a complete replayable trace: model calls with prompt version, messages, response, tokens and latency; tool calls with arguments, result, error, duration and cost; decisions; and state transitions. OpenTelemetry spans mirror the same execution structure.

## Decision
The engine records every model interaction and supports deterministic replay through a `ReplayModel`. During replay, the workflow runs against recorded model responses rather than a live provider. Replay is combined with the seeded evaluation harness: 86 scenarios across the three workflows, scored for task success, tool-selection accuracy, unauthorised-attempt handling, budget adherence, approval correctness and latency/cost. The regression gate compares current results with a stored baseline.

The current completed project passes 86/86 scenarios with all scored metrics at 1.00, mean scenario latency around 36ms, 8 unauthorised attempts blocked, and the refund workflow using 0 model tokens for deterministic eligibility.

## Options Considered
| Option | Pros | Cons |
| --- | --- | --- |
| Trace replay plus eval regression gate | Deterministic, offline CI, catches behaviour and policy regressions, debuggable | Requires careful trace schema and prompt-version recording |
| Live-model tests in CI | Exercises provider integration | Flaky, costly, slow, not reproducible |
| Snapshot-only tests | Cheap and easy | Misses control-flow, policy and budget regressions |
| Unit tests only | Fast and focused | Insufficient for end-to-end agent behaviour |

## Consequences
Positive consequences: behavioural regressions are caught without network access; prompt changes can be evaluated against the same scenarios; prompt versions used in a run are auditable; provider adapters can remain behind configuration and be unit-tested with stubbed HTTP handlers.

Negative consequences: replay proves compatibility with recorded responses, not that a live model will always behave the same way. The eval set must evolve as workflows and adversarial behaviours evolve.

## Risks
A stale baseline can normalize bad behaviour. The mitigation is to keep baseline updates explicit and reviewable. Another risk is overfitting prompts to the seeded scenarios; the deterministic mock model includes adversarial behaviours such as malformed JSON, hallucinated tools, unauthorised tool attempts, loops, refusal, timeout and oversized output to broaden coverage.

## Alternatives Rejected
We rejected live-model-only regression testing because it undermines repeatability and offline operation. We rejected snapshots as the main mechanism because agent correctness is about decisions, constraints and side effects, not just text shape.
