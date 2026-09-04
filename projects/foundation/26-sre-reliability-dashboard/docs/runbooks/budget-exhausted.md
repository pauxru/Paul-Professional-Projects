# Runbook: Error Budget Exhausted

## Trigger
`GET /api/v1/gates/{service}/deploy` returns `allowed: false` and `action: FreezeAllChanges`, or SLO status reports zero remaining budget.

## Immediate actions
1. Confirm the service, SLO window, SLI filter, and metric freshness; preserve the correlation ID.
2. Check `/api/v1/burn-rates?sloId={id}` and `/api/v1/alerts` for active rule state.
3. Pause non-emergency changes. The gate is a safety control, not a diagnosis.
4. If customer impact is active, declare an incident, identify commander/comms lead, and link firing alerts.

## Stabilize and recover
1. Use the owning service runbook and dependency graph to isolate error/latency source.
2. Record mitigation separately from resolution.
3. Evaluate alerts after recovery; confirm both long and short burn windows fall below thresholds before treating paging as recovered.
4. Resolve the incident only after monitoring shows stable service.

## Follow-up
Create a postmortem with automatic incident-window budget attribution; assign owners/due dates; review whether the SLI or policy was correctly scoped. Do not silently override the gate without recorded emergency governance in a real environment.
