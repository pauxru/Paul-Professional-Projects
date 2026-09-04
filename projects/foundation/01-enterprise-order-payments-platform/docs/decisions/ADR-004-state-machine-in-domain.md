# ADR-004 — State machine inside the domain aggregate vs external workflow engine

- Status: Accepted
- Date: 2025-11-24
- Deciders: @pauxru (solo)

## Context

An `Order` has a finite lifecycle:
`Draft → Pending → AwaitingPayment → Paid → Fulfilled | Cancelled | Refunded | PartiallyRefunded`.

A `PaymentIntent` has its own:
`Requires → Authorized → Captured` (with sink states `Voided`, `Failed`).

These transitions must be enforced consistently, must be unit-testable in
isolation, and must throw a clean domain error when violated.

## Options

1. **Anemic model + services** — `Order.Status = OrderStatus.Paid;` set from
   an outer service. Cheap but leaks invariants into every caller.
2. **State machine in the aggregate** — `Order.MarkPending()` /
   `MarkAwaitingPayment(Guid intentId)` / `MarkPaid(...)` / `Cancel(...)`.
   Each method encodes the legal set of predecessor states and raises a
   `DomainException` otherwise.
3. **External workflow engine** — Temporal, Elsa, MassTransit Saga. The state
   lives outside the aggregate; the engine advances it. Powerful but
   requires infrastructure we do not have, obscures the domain, and pushes
   the invariants across a network boundary.

## Decision

**State machines live inside the aggregate (Option 2).**

- Each transition is a named method on the aggregate.
- The method validates the current status, applies the transition, updates
  `UpdatedAtUtc`, increments `Version` (concurrency token), and raises a
  domain event.
- Illegal transitions throw `DomainException(code, message)` — never mutate
  state. The API middleware translates that to a `422 Unprocessable Entity`
  ProblemDetails.

The full transition matrix is unit-tested in
`Contoso.Payments.UnitTests/Domain/OrderStateMachineTests.cs`.

## Consequences

- **Positive** — invariants live in one place, in the language of the
  business, and are enforced regardless of which caller crossed the boundary.
- **Positive** — unit tests are pure — no database, no HTTP, no time. Every
  transition is a two-line test.
- **Positive** — the aggregate + its unit tests double as reference
  documentation for what states can exist.
- **Negative** — long-running orchestrations (waiting for a webhook for
  hours, for example) are not modelled by the aggregate. They live in the
  application layer, where the webhook handler advances the aggregate on
  arrival. That is a fair split; the aggregate stays sync + pure.

## Risks

- A transition method that forgets to raise the right event silently drops
  downstream reactions. Mitigated by test coverage requiring
  `Order.DequeueEvents()` after each transition asserts the emitted event.
- Adding a new state requires touching the transition matrix + all its
  tests. This is the cost of correctness.

## Alternatives revisited

If we introduce a genuinely long-running human-in-the-loop process
(chargeback dispute across days), a workflow engine becomes attractive. The
aggregate transitions would remain the terminal step of each workflow node.
