# ADR-004 — Timezone handling

**Status:** Accepted
**Date:** 2026-09-01

## Context
The platform runs multi-site clinics. Each site has a business timezone (IANA id). Store
times must be timezone-correct across sites and across DST transitions. Clients (dashboard,
patient replies) expect times in the site's local zone; storage must be unambiguous.

## Options
1. **Store local + tz:** store `local_time` and `tz_id` per row. Ambiguous during DST
   transitions.
2. **Store UTC + tz on facility (this ADR):** every timestamp on every business row is
   `DateTimeOffset` in UTC. Facility carries `TimezoneId`. Presentation layer converts.
3. **Store both UTC and local as computed columns:** duplicates data.

## Decision
Store all business times as UTC `DateTimeOffset`. The `Facility` aggregate carries a
`TimezoneId` (IANA form). All comparisons at the domain and application layers use UTC.
Presentation converts to local when responding to clients.

SQLite subtlety: EF Core's SQLite provider cannot `ORDER BY` `DateTimeOffset` directly.
We register a global `ValueConverter<DateTimeOffset, long>` that stores as ticks
(`v.ToUniversalTime().Ticks`), which allows lexicographic ordering. Round-trip is exact
for UTC values.

Availability generation converts each candidate slot to the facility's IANA zone using
`TimeZoneInfo.FindSystemTimeZoneById` when reporting. This is what makes DST correct: at
the March DST transition in London the "10:00 local" slot converts to different UTC times
before and after the switch, but the algorithm reasons in local business time throughout
the day.

## Consequences
- Storage is unambiguous.
- Reads always require a tz conversion if presented locally. Cheap per row.
- DST transitions are handled implicitly by `TimeZoneInfo`; no manual DST tables.

## Risks
- If a facility's `TimezoneId` is changed after appointments exist, past-appointment
  local times shift. Mitigation: treat `TimezoneId` as write-once (enforced in
  `Facility.Create`; there is no setter).
- Windows and Linux use different tz backend paths; verified in tests using
  `TimeZoneInfo.FindSystemTimeZoneById("Europe/London")` which works on both via
  ICU on modern .NET.

## Alternatives considered
- Local + tz per row: rejected for DST ambiguity.
- NodaTime: excellent but pulls in a dependency and its API surface is a training cost;
  BCL is enough here.
