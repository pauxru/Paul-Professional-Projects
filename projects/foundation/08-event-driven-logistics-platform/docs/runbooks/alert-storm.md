# Runbook — Alert Storm

## Trigger

- `logistics.alerts.emitted` rises sharply.
- A single vehicle/type dominates the feed.
- Operators report repeated route-deviation/geofence alerts.

## Triage

1. Identify alert type, context fingerprint, vehicles and first occurrence.
2. Compare raw ping sequence/device timestamps for duplicates, clock jumps and GPS jitter.
3. Check late-arrival and content-conflict counts.
4. For route deviation, verify route polyline and configured corridor.
5. For geofences, verify shape, boundary points and dwell configuration.
6. For offline alerts, verify device connectivity and `OfflineAfterSeconds`.

## Containment

- Do not delete historical alerts or source pings.
- Pause a faulty simulator/replay.
- Revoke or throttle a compromised/misconfigured device.
- Temporarily widen a demonstrably wrong corridor/dwell only through reviewed configuration; record old/new values.
- The in-memory suppression window and unique alert fingerprint should contain repeats. If they do not, capture fingerprints before restart.

## Recovery

1. Correct device clock/route/geofence/rule configuration.
2. Run the relevant unit/property tests.
3. Deploy/restart.
4. Replay the smallest affected window at max speed.
5. Confirm no duplicate alerts and that true alerts still emit.
6. Monitor alert rate and consumer lag for at least two suppression windows.

## Follow-up

Document root cause, false-positive count, affected vehicles, why suppression did or did not work, and whether a new rule-version/fingerprint dimension is needed. Never include unnecessary precise location data in a broad post-incident document.
