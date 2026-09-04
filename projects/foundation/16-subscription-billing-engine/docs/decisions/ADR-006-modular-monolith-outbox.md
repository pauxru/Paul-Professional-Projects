# ADR-006 — Modular monolith with an outbox boundary

## Context

Billing needs atomic financial writes and reliable integration events, but does not inherently require independently deployed services.

## Options

1. Separate catalogue, usage, invoice, payment, and reporting microservices.
2. A single unstructured web project.
3. A layered modular monolith with ports and a persisted outbound queue.

## Decision

Use project-reference-enforced layers and one relational transaction boundary. External dependencies are application-owned interfaces with local adapters. Outbound webhook records are persisted and dispatched asynchronously with retries and dead-letter state.

## Consequences

Financial invariants stay local, the repository runs without infrastructure, and production adapters remain replaceable. Deployment scales as one unit until measured constraints justify extraction.

## Risks

Poor internal discipline could couple modules. Project references, domain purity, and explicit adapter interfaces mitigate that risk.

## Alternatives

Microservices would add distributed transactions, event ordering, and operational load before those costs are justified. A single project would weaken dependency direction and testing clarity.
