# Runbook — Alert Storm

## Symptoms
Many alerts appear for the same asset or metric, frequently transitioning around a threshold.

## Contain
1. Check whether planned maintenance is active. Apply a narrowly scoped maintenance silence only for the affected rule/device.
2. Do not globally suppress alerts before checking whether the affected equipment has a physical safety issue.
3. Use active-alert grouping by device/rule and inspect the first firing timestamp, not only the latest dashboard state.

## Diagnose
1. Inspect raw telemetry and minute rollups for sensor dropout, spikes, drift, or gateway replay.
2. Verify threshold, hysteresis, dwell time, and suppression window; identify any per-asset override.
3. Inspect missing-data rule timing and device/gateway connectivity.
4. Confirm whether the simulator/device firmware changed, then inspect twin desired/reported versions.

## Recover
Tune a rule through the authenticated rules API using measured data: add dwell and hysteresis to avoid flapping, or use a scoped suppression window for repeated incidents. Remove maintenance silencing after verification. Acknowledge alerts only after an operator has investigated; resolution should come from a recovery reading or a documented maintenance transition.

## Follow-up
Record the event in the audit path, add a regression test if a rule configuration caused unexpected behavior, and evaluate whether a seasonal baseline is more suitable than a static threshold.
