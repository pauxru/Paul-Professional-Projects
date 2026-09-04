# ADR-005: Model flag lifecycle and technical debt explicitly

## Context
Flags often become permanent accidental complexity. Operators need to distinguish new, active, deprecated, and archived flags and identify configurations with no recent use or overdue removal dates.

## Options
1. Treat flags as timeless configuration.
2. Delete old flags manually with no state or reporting.
3. Persist lifecycle and removal metadata and generate a stale/debt report.

## Decision
Use `New`, `Active`, `Deprecated`, and `Archived` lifecycle state plus scheduled activation/deactivation, temporary expiry, removal due dates, and exact last-evaluation reporting.

## Consequences
Teams can identify flags that are inactive, stale, or overdue before cleanup. Archived flags evaluate to their off variation, preserving safe behavior during removal.

## Risks
Exact analytics rows need retention management. Lifecycle status is metadata, so process discipline is still required to set removal dates and act on reports.

## Alternatives
A background auto-delete job could reduce manual work but is unsafe without ownership/ticket integration. HyperLogLog is preferable when exact unique counts become expensive.
