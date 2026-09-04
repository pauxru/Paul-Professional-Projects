# ADR-003 — Use Canary Gates Within a Blue/Green Revision Model

## Context

The team needs fast rollback, controlled exposure and objective promotion criteria. Container Apps can keep multiple active revisions and split traffic.

## Options

1. Rolling replacement.
2. Instant blue/green cutover.
3. Percentage canary.
4. Canary traffic steps between stable and candidate revisions.

## Decision

Keep stable and candidate revisions active. Shift candidate traffic through 5%, 20%, 50% and 100%. At each step query readiness and metrics. Promote only when sample count, error rate and p95 latency pass; missing data means rollback.

## Consequences

- Rollback is a traffic operation measured in minutes or less.
- The candidate exercises real traffic before full exposure.
- A feature flag can dark-launch logic before serving its result.
- The migration must remain backward compatible while both revisions run.

## Risks

- Low-volume environments may not collect enough samples.
- Aggregate metrics can hide cohort-specific errors.
- A shared database means schema-destructive rollback is not automatic.

## Alternatives

Instant blue/green remains useful for deterministic internal services. Rolling replacement is simpler but mixes versions without an explicit gate. A long-lived canary may be appropriate at higher scale, with richer cohort and SLO analysis.
