# ADR-005: Persist a resumable rotation state machine

Status: Accepted

## Context

Rotation spans generation, persistence, notification, human/application acknowledgement,
verification, and promotion. A process can stop between any two actions. Restarting from the
beginning can create duplicate credentials or send conflicting notices.

## Options

1. Execute the whole workflow in one HTTP request with in-memory state.
2. Store only a final success/failure record.
3. Persist each explicit state transition and idempotency key.

## Decision

Use option 3. `AdvanceOneStepAsync` performs at most one logical transition and saves it.
`RunToPauseOrTerminalAsync` repeatedly invokes that primitive until the operation is terminal or
must wait. A fresh engine can reload and continue from the stored state.

## Consequences

Crash recovery is testable and operations are observable while in progress. Side effects need
idempotent design. The state model is more verbose than a procedural method.

## Risks

The local single-process implementation does not lease work across nodes. A crash after an external
notification but before its local sent timestamp can redeliver; consumers must treat notices as
idempotent. Production needs compare-and-swap transitions and a transactional outbox.

## Alternatives

An in-memory workflow was rejected because process loss loses intent. A final-only record was
rejected because it cannot resume or explain where and why a rotation stopped.
