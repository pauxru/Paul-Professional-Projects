# ADR-004 — Enforce delegated authority and four-eyes approval

## Context
Large exposure approvals should not be made unilaterally, and role names alone do not express monetary authority.

## Options
1. Trust an underwriter’s role claim without amount checks.
2. Require two approvals for every loan.
3. Enforce role-specific limits and require a distinct second approver above a configured exposure threshold.

## Decision
Use option 3. Junior, Senior, and Credit Committee limits are explicit; exposure above KES 500,000 requires an authorized second approver with a different identity. The recorded decision includes both actors and reason.

## Consequences
The control is testable and the queue can preserve who claimed work. Low-value applications avoid needless dual approval friction.

## Risks
The in-process default policy is configuration, not a complete organization/entitlement hierarchy. A real deployment would use policy administration, active delegation expiry, and separation-of-duty reporting.

## Alternatives
An external workflow engine or role-only authorization could replace this, but both would still need amount-aware authorization semantics.
