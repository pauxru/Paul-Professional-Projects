# ADR-002: Two bits per step, not one

## Status
Accepted. Forced by a false counterexample -- see `portfolio/04`.

## Context
The orchestrator needs to record, per step, whether its effect is present. The
obvious representation is one bit.

It is wrong. A non-idempotent step can land twice: call, timeout, retry, and now
the effect has been applied twice while the orchestrator believes it may have
been applied zero times. Compensate once and a single bit flips to "absent" while
the effect is still present, once over.

## Decision
Two bits per step, saturating, packed into a single `int` field on `Config`.

Two bits because the meaningful distinction is zero / exactly one / more than
one. A third value would multiply the state space without changing any property's
verdict.

## Consequences
`Config.MaxSteps` is 15. The `Saga` constructor rejects anything longer rather
than silently corrupting a neighbouring step's counter -- a failure mode where the
*state space* is wrong rather than the saga, which no property could detect.

The packing keeps `Config` a value type with cheap equality and hashing, which
matters at ~2,000 states per check and ~130,000 for the longest synthetic chain.

## Note
The bug this fixes is not exotic. A `bool[] completed` in an orchestrator's
durable state is the same representation, and it is what most of them have.
