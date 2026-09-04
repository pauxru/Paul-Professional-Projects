# ADR-001: Evaluate flags locally in the .NET SDK

## Context
A remote evaluation request on every caller request couples application availability and latency to the control plane. The project also needs private-attribute handling and useful offline behavior.

## Options
1. Remote-evaluate every variation request.
2. Bootstrap rules and evaluate locally in each SDK.
3. Expose only boolean values through a push service.

## Decision
Use a bootstrap ruleset plus shared Domain evaluator in the SDK. The SDK receives SSE notifications, conditionally fetches a new snapshot, persists last-known-good configuration, and evaluates synchronously in-process.

## Consequences
Caller latency has no network hop and server/SDK semantics are deliberately identical. Rules are shipped to authorized clients, so client-key exposure is restricted to `ClientSide` flags.

## Risks
A client can temporarily evaluate a stale snapshot during disconnection. Configuration may disclose unreleased client-side feature intent if flags are incorrectly marked client-side.

## Alternatives
Remote evaluation is suitable for centrally protected attributes or rapidly changing rules. A hybrid model could offer both modes per flag.
