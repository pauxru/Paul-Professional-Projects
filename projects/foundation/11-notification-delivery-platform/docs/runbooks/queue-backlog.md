# Runbook — Queue backlog

## Symptoms

- `notifications_queued_total` climbing but `notifications_sent_total`
  static.
- `GET /api/v1/analytics/summary` shows `queued` growing faster than
  `sent`.

## First checks

- Is the hosted worker running? `GET /health/ready` should return 200.
- Are any providers open on the circuit breaker for the busy channel? If
  all are open, notifications will re-schedule for later.
- Is the database file locked or blocked? On SQLite you would see writer
  timeouts in the logs.

## Contain

- Lower the per-tenant `NotificationOptions.BulkMaxItems` so incoming
  volume shrinks while you drain.
- If the backlog is concentrated in one tenant, reduce that tenant's
  weight in the fairness scheduler (config value) so other tenants
  continue to flow.

## Recover

- If providers were the bottleneck, they will drain on their own once
  circuits close. Watch `notifications_sent_total`.
- If the worker itself is the bottleneck (all providers healthy but sends
  are slow), consider increasing the pipeline batch size or running a
  second worker instance — this requires the multi-instance changes noted
  in Known Limitations.

## Follow up

- Set an alarm on `queued / sent` ratio over a 5-minute window.
- Consider auto-scaling the worker (post-single-instance).
