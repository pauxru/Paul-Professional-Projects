# ADR-005 — Billing provider simulator with signed webhooks

**Status:** Accepted
**Date:** 2026-09-03

## Context

The portfolio must demonstrate subscription creation, plan changes, proration, invoice/payment events and dunning without calling paid APIs or storing payment credentials.

## Options

1. Integrate a real billing sandbox.
2. Mock billing calls only in tests.
3. Implement a deterministic `IBillingProvider` simulator and real webhook security pipeline.
4. Omit billing state from the application.

## Decision

Implement a local provider adapter that persists synthetic customers/subscriptions, calculates remaining-month proration in KES/USD and signs event payloads. Webhook processing verifies HMAC-SHA256 over `timestamp.rawBody`, enforces a five-minute window, compares in constant time and records unique event IDs before state transition.

## Consequences

- The full integration is runnable offline and repeatable.
- Forgery, staleness, replay, dunning and recovery are covered by tests.
- No payment card data enters the system.
- Pricing, tax and accounting semantics are intentionally simplified.

## Risks

- A simulator cannot reveal vendor availability or schema quirks.
- Event ordering beyond duplicate IDs is not modeled.
- A real provider may require key rotation and multiple active secrets.

## Alternatives

A vendor sandbox adds network/credential fragility to evaluation. Pure mocks would not exercise raw-body signature handling or idempotent receipts. Production should retain `IBillingProvider` while adding vendor adapters, secret rotation, event ordering rules and reconciliation jobs.
