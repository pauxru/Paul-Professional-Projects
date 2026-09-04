# ADR-003 — Central entitlement and quota service

**Status:** Accepted
**Date:** 2026-09-03

## Context

Plan checks scattered through endpoints drift and produce inconsistent upgrade responses. Capacity and feature checks need one commercial vocabulary.

## Options

1. Hard-code plan checks in each endpoint.
2. Store arbitrary JSON rules and interpret at runtime.
3. Central typed plan catalogue and `IEntitlementService`.
4. Delegate authorization entirely to a billing vendor.

## Decision

Define typed `PlanDefinition` records for Free, Starter, Professional and Enterprise. Endpoints consult `IEntitlementService` for features and numeric limits. Monthly meters use a separate `UsageQuotaService` with soft/hard thresholds and `IClock`.

## Consequences

- Upgrade behavior and limits are reviewable in one place.
- HTTP mapping can consistently emit 402 or 429 ProblemDetails.
- Tests cover boundaries and period rollover without waiting for real time.
- Catalogue changes currently require deployment.

## Risks

- Billing plan and application catalogue could diverge.
- In-process atomicity is insufficient for multiple replicas.
- Consuming quota before a later command failure can over-count.

## Alternatives

Dynamic rules offer no-deploy changes but need schema/version governance and safer expression evaluation. Vendor-only checks couple core authorization to network availability. A production evolution would version entitlements and reconcile them against billing events.
