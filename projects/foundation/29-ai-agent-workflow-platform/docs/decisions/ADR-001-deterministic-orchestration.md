# ADR-001: Deterministic orchestration with a model-in-the-loop vs model-driven planning

## Title
Deterministic orchestration with a model-in-the-loop vs model-driven planning

## Status
Accepted

## Context
This platform orchestrates agentic workflows in a domain where repeatability, auditability and bounded failure modes matter more than open-ended autonomy. The system supports language-heavy work, but completed runs must be explainable, replayable and regression-testable offline. The project therefore uses declarative, versioned workflow graphs composed of typed steps: `ModelStep`, `ToolStep`, `ConditionStep`, `ParallelStep`, capped `LoopStep`, `HumanApprovalStep`, `TransformStep` and `TerminalStep`.

A common alternative is a ReAct-style or planner-style agent where the model repeatedly decides the next action, chooses tools and determines when the task is complete. That pattern is flexible, but it makes control flow probabilistic and moves too much authority into model output. In this project, even an adversarial or malformed model response must not be able to reroute execution into arbitrary side effects.

## Decision
Control flow is owned by a deterministic, validated workflow state machine. The model is in the loop for bounded language sub-tasks such as classification, summarisation, extraction and drafting, but it never chooses the next step, bypasses validation or executes side effects directly.

Branching, eligibility and validation are deterministic code. For example, refund eligibility is computed by `RefundEligibilityCalculator` in the Domain layer, not by a prompt. The refund workflow intentionally uses zero model tokens for eligibility because eligibility has a correct answer and belongs in code.

## Options Considered
| Option | Pros | Cons |
| --- | --- | --- |
| Deterministic workflow state machine with model sub-tasks | Predictable execution, replayable traces, offline regression tests, bounded blast radius, auditable decisions | Less open-ended; new control paths require workflow changes |
| ReAct/model-planner agent controls next action | Flexible and quick to prototype; can adapt to underspecified tasks | Non-deterministic control flow, harder replay, prompt injection has more authority, difficult approval and budget semantics |
| Fully deterministic automation with no model | Maximum predictability and low cost | Poor fit for fuzzy language interpretation and drafting |

## Consequences
Positive consequences: run behaviour is explainable from persisted state transitions; deterministic replay is meaningful; tests can assert exact branches; OpenTelemetry spans mirror a known state machine; approval and compensation hooks have stable placement.

Negative consequences: workflow authors must model branches explicitly. Some exploratory tasks require more design effort than a free-form agent loop. The platform trades some agent flexibility for operational clarity.

## Risks
The main risk is over-constraining workflows so much that teams reintroduce model-driven planning outside the engine. The mitigation is to provide expressive typed step primitives, versioned workflows and enough model integration for real language tasks without delegating authority over control flow.

## Alternatives Rejected
We rejected model-owned planning for production orchestration because it weakens predictability, replay and auditability. We also rejected prompt-only guardrails as a control-flow mechanism; prompt wording is not a security boundary.
