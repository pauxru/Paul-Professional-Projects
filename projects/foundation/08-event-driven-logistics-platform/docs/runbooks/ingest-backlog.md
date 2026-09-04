# Runbook — Ingest Backlog

## Trigger

- `logistics.consumer.lag` grows for more than two observation windows.
- `logistics.processing.lag.ms` breaches the operational objective.
- API latency/429 responses rise or channel publishers block.
- Dead letters or SQLite lock/disk errors appear.

## Immediate checks

1. Call `GET /health/ready`.
2. Call `GET /api/v1/telemetry/stats` with an `operations` token.
3. Compare stored ping growth with processed counter growth.
4. Inspect whether one vehicle/partition dominates input.
5. Check disk capacity, SQLite write errors and dead-letter reason samples.
6. Confirm a replay job or simulator is not competing with live ingestion.

## Containment

- Stop/pause simulator and non-essential replay jobs.
- Preserve source pings; do not delete the event table to reduce lag.
- If abuse is suspected, revoke the device credential and reduce its gateway quota.
- In local mode, restart only after confirming retained pings are present; the in-memory queue is recoverable from SQLite replay.

## Recovery

1. Resolve the slow dependency/rule or storage issue.
2. Start the service and verify readiness.
3. Rebuild the current projection if messages were accepted but not processed:

```powershell
Invoke-RestMethod -Method Post -Headers $headers `
  -Uri http://localhost:5008/api/v1/replay/rebuild-projection
```

4. Watch consumer lag return to zero and processing lag normalize.
5. Verify dead-letter and late-arrival counts are no longer increasing.

## Escalation evidence

Capture correlation IDs, time range, affected vehicle IDs, lag/throughput graphs, dead-letter reasons, deploy/config version and whether replay was active. Do not attach raw location history to a broad incident channel.

## Production prevention

Autoscale broker consumers by partition lag, isolate replay consumer groups, cap per-device rates, route retries/DLQ explicitly, range-partition storage and alert on hot-key skew.
