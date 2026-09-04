# ADR-003 — At-least-once execution with idempotent loads

## Context

An HTTP timeout cannot prove whether a target committed a write. Exactly-once delivery across independent products is not generally achievable without shared transactions.

## Options

1. At-most-once: never retry ambiguous writes.
2. Claim exactly-once through distributed coordination.
3. Use at-least-once attempts with stable idempotency keys and stored responses.

## Decision

Choose option 3. The hub derives a stable key from flow, load step, and source record identity. It stores the first successful result and sends the same key to targets and replay operations.

## Consequences

Transient failures and operator replay are safe when the adapter and target honor the contract. Previously successful calls return their original result.

## Risks

Targets without idempotency support can still duplicate effects after an ambiguous response. Such connectors require a provider lookup/reconciliation strategy.

## Alternatives

At-most-once avoids duplicates but loses legitimate writes. Two-phase commit is unavailable across typical SaaS APIs and would increase coupling.
