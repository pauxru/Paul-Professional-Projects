# ADR-003 — On-demand slot generation

**Status:** Accepted
**Date:** 2026-09-01

## Context
Availability search needs to return, for a facility / clinician / date / appointment-type,
every start time at which a booking is possible. The inputs are working patterns, leave,
holidays, existing appointments, room capabilities, and appointment-type buffers.

## Options
1. **Precomputed slots:** at midnight (or on any working-pattern / leave / holiday change)
   generate every slot for the next N days into a `slot` table. Query is trivial.
2. **On-demand generation (this ADR):** compute available slots at query time from the
   underlying tables. No slot table, no caching.
3. **Hybrid:** precompute a "candidate" grid, filter on demand for existing bookings.

## Decision
Generate slots on demand from working patterns, leave, holidays and existing appointments
using `AvailabilityService.SearchAsync(...)`. The algorithm:

1. Enumerate the working patterns for the clinician on the requested day.
2. Intersect with facility operating hours.
3. Subtract closures (holidays) that overlap the day.
4. Subtract leave periods that overlap the day.
5. Load candidate rooms (matching `RequiredCapability` of the appointment type).
6. Enumerate candidate start times at 15-minute granularity from window start.
7. For each candidate: reject if it overlaps an existing non-cancelled appointment for
   the same clinician, or if no room is free for the duration + buffer.

Cost: O(candidates × log(existing)) with the `(ClinicianId, StartUtc)` index; well within
budget for operational use.

## Consequences
- **Correctness.** No cache-invalidation problem: because we never cache, we never
  serve stale slots. Any change to working patterns, leave, or bookings is reflected on
  the next query.
- **Latency.** Each search hits the DB two-to-three times. On SQLite this is fast; on
  Postgres, a partial index on `(clinician_id, start_utc) WHERE status NOT IN (7,8)`
  makes it faster still.
- **Simplicity.** There is no slot-table schema, no compaction job, no delta-update job.

## Risks
- Under very high load, on-demand generation could become expensive. Mitigation:
  we can move to hybrid by materialising a rolling 14-day candidate grid.

## Alternatives considered
- Precomputed slots: rejected because cache invalidation on working-pattern change would
  need to purge and regenerate large slot ranges.
- Hybrid: rejected for now as premature optimisation; kept as a future migration path.
