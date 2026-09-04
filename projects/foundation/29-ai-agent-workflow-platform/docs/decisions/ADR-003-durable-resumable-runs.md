# ADR-003: Durable, resumable run persistence with per-step checkpointing

## Title
Durable, resumable run persistence with per-step checkpointing

## Status
Accepted

## Context
Agent workflows can span model calls, tool calls, approvals, retries and timeouts. A process crash, deployment restart or infrastructure interruption must not force the platform to either lose work or restart a mutating workflow from the beginning. The completed project uses EF Core with SQLite as the default persistence layer, but the architectural concern is broader: run progress is part of the system of record.

The engine already models workflows as explicit state machines. That state machine only becomes operationally useful if run state, current position and step outcomes are persisted at the right boundaries.

## Decision
The engine persists run state and position after each step. Every step execution records status, inputs, outputs, errors, timing and transition information sufficient to resume exactly where the run stopped. A crashed run is recovered by loading persisted state and continuing from the next eligible transition rather than re-planning or replaying side effects.

Mutating tool execution is protected with idempotency keys so a retry after a partial failure does not double-execute the mutation. Per-step retry with backoff is applied only to classified transient failures. Non-transient failures, budget violations, approval pauses and cancellations move through explicit state transitions.

This behaviour is covered by a simulated-crash test that proves the engine resumes from the persisted checkpoint rather than starting over.

## Options Considered
| Option | Pros | Cons |
| --- | --- | --- |
| Per-step durable checkpointing | Crash-safe, auditable, resumable, compatible with approvals and compensation | More persistence complexity; schema matters |
| Persist only final run result | Simple | Loses in-flight work; cannot resume; poor traceability |
| Re-run from start after crash | Easy mental model | Unsafe for mutations; expensive; changes behaviour if model responses differ |
| External queue-only orchestration | Mature patterns for jobs | Still requires domain run state and idempotency; less direct replay model |

## Consequences
Positive consequences: long-running and paused workflows survive restarts; approvals can resume deterministically; retry policy is explicit; traces line up with persisted execution records; mutating tools can be reasoned about using at-most-once semantics.

Negative consequences: the system must handle persistence migrations carefully. Developers must define what is persisted for every new step type and tool.

## Risks
If a step writes external state before its local checkpoint is committed, recovery could retry an already-completed mutation. The idempotency-key requirement mitigates this for mutating tools. Another risk is incorrectly classifying failures as transient; retries are therefore limited and typed rather than blanket retries.

## Alternatives Rejected
We rejected best-effort in-memory orchestration because it fails the crash-resume requirement. We rejected replaying from the beginning as a recovery strategy because it conflicts with at-most-once mutation, deterministic audit and cost control.
