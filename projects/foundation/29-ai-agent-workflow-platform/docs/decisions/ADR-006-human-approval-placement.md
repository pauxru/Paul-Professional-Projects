# ADR-006: Placement of human-in-the-loop approvals for mutating/external actions

## Title
Placement of human-in-the-loop approvals for mutating/external actions

## Status
Accepted

## Context
Some actions should not be executed solely because an automated workflow reached them. Mutating or external actions can create confused-deputy failures: a model or retrieved content persuades the system to use its legitimate authority for an unintended purpose. The project therefore treats approval as a runtime control placed at the action boundary, not as a vague earlier review of the conversation.

The engine already has typed tools, risk levels, per-step allow-lists, idempotency keys and complete traces. Human approval must integrate with those mechanisms without making resumption non-deterministic.

## Decision
Mutating or external tool calls above a configured risk threshold are gated behind an explicit `HumanApprovalStep`. The run pauses before executing the action. The approval task contains the full proposed action, typed arguments and reasoning trace needed for review. An approver with the `agents:approve` scope can approve, reject, or modify-and-approve. Approval decisions are audited. If the approval times out, the configured default action is applied.

After approval, the run resumes deterministically from the persisted state. Mutating execution remains idempotent: the approved action is executed with the same at-most-once controls and idempotency keys used by retries.

## Options Considered
| Option | Pros | Cons |
| --- | --- | --- |
| Approval at the action boundary | Reviews concrete action and arguments, blocks confused-deputy execution, deterministic resume | Adds latency and operational workflow |
| Approval at workflow start | Low interruption | Reviewer cannot inspect the actual proposed action or model/tool context |
| Approval after execution | Preserves automation speed | Not a safety control; only audit/remediation |
| Model self-approval | Fully automated | No independent authority boundary |

## Consequences
Positive consequences: high-risk actions are reviewed when they are specific and actionable; rejected or modified actions are traceable; approval requirements can be tested; the state machine has a clear pause/resume point; permission checks are explicit through `agents:approve`.

Negative consequences: some runs pause and require human response. Timeout defaults must be chosen carefully because they encode business risk appetite.

## Risks
Approvers may rubber-stamp actions if the approval payload is noisy. The mitigation is to include the proposed action, arguments and relevant reasoning trace, not an undifferentiated log dump. Another risk is executing a modified approval twice after retry; idempotency keys and persisted step records mitigate this.

## Alternatives Rejected
We rejected early blanket approvals because they do not address prompt injection that appears later through retrieved or tool content. We rejected post-action approval because it is audit, not prevention. We rejected model-only approval because it collapses the same trust boundary the approval step exists to enforce.
