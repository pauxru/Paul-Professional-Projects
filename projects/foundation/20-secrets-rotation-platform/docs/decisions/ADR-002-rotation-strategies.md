# ADR-002: Dual-write by default, explicit single-cutover fallback

Status: Accepted

## Context

Targets vary: many accept two credentials concurrently, while some legacy systems accept only one.
A single universal workflow either wastes safe overlap or pretends overlap exists where it cannot.

## Options

1. Always replace immediately.
2. Always require dual validity.
3. Implement pluggable strategies with common persisted states.

## Decision

Use option 3. `DualWriteRotationStrategy` requires consumer acknowledgements before promotion and
retains the old current as previous. `SingleCutoverRotationStrategy` sends notices but makes
acknowledgement advisory and cannot promote before an explicit maintenance window.

## Consequences

Most rotations gain a safe migration window. Legacy targets remain representable without hidden
special cases in the engine. Operators must choose the strategy and supply a window when required.

## Risks

An incorrect strategy choice can cause downtime. A target may claim dual-key support but implement
it inconsistently. Single-cutover concentrates risk in the maintenance window.

## Alternatives

Immediate replacement was rejected as unsafe by default. Dual-write-only was rejected because it
cannot model common database/vendor constraints. Per-type hard-coding was rejected in favor of a
strategy port.
