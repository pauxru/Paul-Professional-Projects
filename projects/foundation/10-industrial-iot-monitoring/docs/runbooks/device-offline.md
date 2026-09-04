# Runbook — Device Offline / Gateway WAN Loss

## Symptoms
- Dashboard reports a growing gateway buffer depth or the demo network link is OFFLINE.
- A device heartbeat/missing-data rule fires.
- MQTT last-will status reports `offline`.

## Immediate safety action
Do not treat this application as a safety system. Follow the plant's approved physical safety procedure before attempting remote restart or control actions.

## Diagnose
1. Check `GET /health/live` and `/health/ready`.
2. Check the gateway buffer-depth metric and the active alerts endpoint.
3. Distinguish local device loss (no MQTT telemetry / LWT) from a cloud-link loss (edge buffer increases while local safety rules continue).
4. Verify device status, revocation state, credential rotation history, and clock skew.

## Recover
1. Restore network path or broker availability; do not delete the local SQLite edge database.
2. Set/recover the gateway cloud link. In the demo, use the dashboard **Restore network** button or authenticated `POST /api/v1/demo/network` with `{ "online": true }`.
3. Confirm the queue drains in sequence and that ingestion receipts show accepted or duplicate records.
4. Query raw telemetry over the outage time range; acknowledge and resolve alerts only after operational verification.

## Escalate
Escalate if buffer capacity is near exhaustion, queue eviction occurs, credentials are suspected compromised, or a device remains offline after physical/network inspection. Preserve the SQLite queue and logs for analysis.
