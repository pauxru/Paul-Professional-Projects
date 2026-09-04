# Runbook — Replay an Operational Incident

## Purpose

Reproduce rule/projection behaviour for a retained telemetry window without inserting duplicate source pings or alerts.

## Preconditions

- Incident responder has `operations` scope.
- UTC start/end and optional vehicle ID are known.
- Current alert count and relevant trip/state are captured.
- Live ingest lag is healthy; postpone broad replay during an existing backlog.

## Execute

Use maximum speed for deterministic diagnosis:

```powershell
$body = @{
  from = '2026-09-03T06:00:00Z'
  to = '2026-09-03T06:15:00Z'
  speed = 0
  vehicleId = '20000000-0000-0000-0000-000000000001'
} | ConvertTo-Json

Invoke-RestMethod -Method Post -Headers $headers -ContentType application/json `
  -Uri http://localhost:5008/api/v1/replay -Body $body
```

Use `speed = 10` or `speed = 1` when human observation of timing is part of the incident.

## Verify

1. `eventsRead == eventsPublished`.
2. Consumer lag returns to zero.
3. Dead-letter count does not rise.
4. Alert count/fingerprints remain unchanged for an identical replay.
5. Correlation ID and replay parameters are retained in the incident record.

## Rebuild corrupted current state

If the incident concerns projection corruption rather than rule diagnosis:

```powershell
Invoke-RestMethod -Method Post -Headers $headers `
  -Uri http://localhost:5008/api/v1/replay/rebuild-projection
```

The operation deletes only `VehicleStates`, clears volatile calculators and republishes retained source events. In a production-sized system, build a shadow projection and swap it after validation.

## Abort criteria

Stop/escalate if consumer lag grows, live ingest is affected, dead letters increase, the requested range is unexpectedly large, or access includes vehicles outside the incident scope.

## Privacy

Limit replay to the smallest vehicle/time range. Treat exported coordinates and screenshots as sensitive location data.
