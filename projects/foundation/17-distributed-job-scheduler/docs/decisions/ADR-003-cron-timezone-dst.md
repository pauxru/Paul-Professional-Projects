# ADR-003 — Cron, timezone and DST strategy

- **Status:** Accepted
- **Date:** 2026-09
- **Context tags:** scheduling, correctness, time

## Context

Recurring triggers need cron. Real operations run across timezones and daylight-saving transitions,
where a naive scheduler either skips runs, double-runs them, or fires at the wrong wall-clock time.
The build must have **no external cron library dependency** carrying licensing/behaviour surprises,
and DST correctness must be *tested*, not assumed.

## Options considered

1. **Third-party cron library (e.g. Cronos/NCrontab/Quartz.NET).** Mature, but adds a dependency,
   varies in field semantics (Vixie OR rule, `L`/`#`), and DST behaviour differs between libraries —
   and the brief explicitly asks to *implement a real cron parser with thorough tests*.
2. **Store schedules as UTC only, ignore local time.** Simple, but wrong for humans: "run at 02:30
   America/New_York" must track DST, not drift by an hour twice a year.
3. **Hand-written cron parser + explicit timezone/DST occurrence engine (chosen).**

## Decision

Implement `CronExpression` (parser + next-occurrence) and compute occurrences in the definition's
**IANA/Windows timezone**, converting to UTC for storage and comparison.

- **Parser** supports 5-field (`min hour dom month dow`) and 6-field (`sec …`) expressions, ranges
  (`1-5`), steps (`*/5`, `0/15`), lists (`1,2,3`), month/day **names**, the **Vixie day-of-month /
  day-of-week OR rule** (when both DOM and DOW are restricted, a match on *either* fires), and the
  optional `L` (last) and `#` (nth weekday) tokens. Invalid expressions throw `CronFormatException`.
- **Timezone/DST** via `TimeZoneResolver` (`TimeZoneInfo`, IANA id with Windows fallback):
  - **Spring-forward (skipped local time):** an occurrence that lands in the non-existent hour is
    rolled forward to the next valid instant (the run is not silently dropped).
  - **Fall-back (repeated local time):** an ambiguous local time fires **once**, on the earlier
    (first) UTC instant, so it is neither skipped nor double-fired.
- **Misfire policies** for occurrences discovered late (scheduler down, leader failover):
  `FireNow` (one run for the whole missed window), `SkipToNext` (ignore misses), `RunAllMissed`
  (enqueue each missed occurrence, bounded by `MaxCatchUp`), plus a `CatchUpWindowSeconds` bound.

## Consequences

- **Positive:** Zero scheduling dependencies; fully specified, testable behaviour. DST correctness
  is pinned by tests using a real transition (`America/New_York`): `CronDstTests` asserts the
  spring-forward skip and the fall-back single-fire; `CronParserTests` checks next-N occurrences
  against hand-computed UTC instants; `TriggerScheduleTests` covers all three misfire policies + the
  catch-up cap.
- **Positive:** The Vixie OR rule and `L`/`#` are handled explicitly, matching operator expectations
  from Unix cron.
- **Negative:** A hand-written parser is code we own and must maintain; edge cases (leap years,
  `L-3`, seconds-field combinatorics) require ongoing test coverage.

## Risks & mitigations

- **Timezone database differences (Windows vs IANA).** Mitigation: `TimeZoneResolver` resolves IANA
  ids with a Windows fallback; `TimeZoneResolverTests` guards resolution.
- **DST rule changes over time.** Mitigation: rely on the OS `TimeZoneInfo` database (patched by the
  platform), not a hard-coded table.
- **Ambiguous-time policy is a choice.** We fire once on the earlier instant; documented so operators
  know a fall-back run is not "missing".

## Alternatives not chosen

Quartz.NET (heavyweight, its own persistence model), UTC-only scheduling (wrong wall-clock behaviour
across DST), or trusting a library's undocumented DST semantics.
