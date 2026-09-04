# ADR-005 — At-least-once delivery with subscriber-side deduplication

- Status: Accepted
- Date: 2025-11-24
- Deciders: @pauxru (solo)

## Context

The transactional outbox (ADR-001) delivers domain events at-least-once by
design: after a message is dispatched, if the dispatcher crashes before
recording success, the same message will be picked up and re-delivered on
the next poll. Subscribers must therefore be idempotent.

The webhook receiver is a form of subscriber (of the *provider*'s events),
and it faces the same requirement — a provider that receives no 200 will
retry.

## Options

1. **Attempt exactly-once via transactional-messaging protocols.** Only some
   brokers support this (Kafka + transactional producers with idempotent
   consumers; RabbitMQ requires app-level coordination). Doesn't compose with
   arbitrary subscribers, and introduces coordination overhead.
2. **At-least-once with subscriber-side dedup.** Each subscriber persists a
   "seen" record keyed on a stable identifier (the event id, or a hash of the
   raw payload). Simple and portable. **Chosen.**
3. **At-most-once (fire-and-forget).** Loses events on any failure. Not
   acceptable for a payment platform.

## Decision

**We use Option 2: at-least-once + subscriber-side dedup.**

For the two subscriber classes we ship:

- **Outbound (our events → external subscribers).** Every `OutboxMessage` has
  a stable `Id` (the domain event id). External subscribers are expected to
  dedup on that id. Our in-process `ChannelEventBus` just publishes; a real
  RabbitMQ/Azure adapter would set the id as the broker message id and rely
  on consumer-side dedup.
- **Inbound webhooks (provider → us).** `WebhookService` stores a SHA256 of
  the `X-Contoso-Signature` header in `WebhookReplayRecord`. A second POST
  with the same signature returns 200 with `replay: true` and does not
  mutate state. This handles both provider retries and replay-attack attempts
  identically.

The dispatcher applies exponential backoff with jitter, giving deterministic
worst-case retry age, and dead-letters after `MaxAttempts` so poison messages
do not stall the queue.

## Consequences

- **Positive** — no distributed protocol required. Works with any broker.
- **Positive** — the same mental model applies to inbound and outbound.
- **Negative** — every subscriber MUST implement dedup. This is a discipline
  requirement, not enforceable at the framework level.
- **Positive** — the "seen" tables provide a real-time audit of every event
  observed by the platform.

## Risks

- If a subscriber forgets to dedup, we get side-effect duplication. Mitigated
  by making dedup the *first* thing every subscriber does — see the
  `WebhookService.ProcessAsync` order: verify → check replay → process.
- Dead-letter re-drive without a fresh id would re-trigger dedup. This is
  intentional; the operator would generate a new id when the payload has
  been genuinely corrected.

## Alternatives revisited

Exactly-once semantics via Kafka + transactional producers would be a
possible future move if we adopt Kafka for cross-service events. The
subscriber-side dedup can remain — it's cheap insurance and it also handles
misbehaving publishers.
