# Runbook — Provider outage

## Symptoms

- `notifications_failed_total{provider="<name>"}` climbs sharply.
- `dlq_depth` rises.
- The ops dashboard shows a provider in `Open` state that does not close.

## Confirm

1. `GET /api/v1/analytics/summary` — check per-provider counters.
2. `GET /api/v1/analytics/providers` — check circuit state and last error.
3. Look at recent structured logs filtered by `provider=<name>` and
   `attempt=*` for the failure reasons.

## Contain

- Do **not** disable the provider; the circuit breaker already stopped
  hammering it. Fail-over to the secondary is happening automatically.
- If the secondary is also failing, priority messages are still eligible
  for retry — check `notifications_queued_total` is not growing without
  bound. If it is, temporarily lower `DefaultMaxAttempts` in configuration
  so notifications drain to the DLQ rather than accumulating.

## Recover

1. When the provider is healthy again, either:
   - do nothing — the half-open probe will close the breaker on the next
     eligible attempt, or
   - force-close it: `POST /api/v1/admin/providers/<name>/reset`.
2. Drain the DLQ if it accumulated: see `dlq-drain.md`.

## Follow up

- File an incident review.
- If the provider was silently degraded (partial failures, high latency,
  no explicit error) consider adding an additional health signal to
  `IChannelProvider` and a metric-driven trip in addition to the
  consecutive-failures trip.
