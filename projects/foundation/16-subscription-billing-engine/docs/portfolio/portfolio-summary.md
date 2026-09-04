# Portfolio Summary

## Classification

Self-directed engineering case study; reusable reference implementation, not client work.

## Problem

SaaS billing must retain historical prices, aggregate immutable usage, calculate mid-cycle changes, collect failed invoices, and resist duplicate/replayed operations without relying on external infrastructure for evaluation.

## Built

A .NET 10 modular monolith with seven pricing strategies, explicit month-end anchors, second-level proration, SQLite invoice idempotency, coupons/credits/tax, payment and dunning simulation, MRR/deferred-revenue helpers, signed webhooks, JWT policies, OpenTelemetry, and API/infrastructure tests.

## Engineering signals

- Minor-unit financial math with documented rounding and reconciliation.
- Append-only plan/usage/audit evidence and finalized invoice immutability.
- Database uniqueness as the concurrency backstop.
- Fake-clock dunning and signed webhook recovery.
- Ports for payment, tax, identity, clock, replay storage, and outbound transport.
- Honest local adapters and unverified Docker disclosure.

## Verification

See `docs/test-results.md` for commands and actual results. No production deployment, real customer, revenue, transaction-volume, or certification claim is made.
