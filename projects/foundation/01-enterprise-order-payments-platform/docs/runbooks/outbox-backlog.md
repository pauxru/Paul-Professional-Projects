# Runbook — outbox backlog

**Symptom.** `SELECT COUNT(*) FROM OutboxMessages WHERE Dispatched = 0` grows
past the alert threshold (or the `outbox_dispatch_duration_ms` histogram is
climbing rapidly, or the `outbox_dead_lettered_total` counter is
incrementing).

## Triage

1. Confirm the dispatcher is running. On a healthy host:
   ```powershell
   Get-Process -Name Contoso.Payments.Api  # should be alive
   Invoke-RestMethod http://localhost:5001/health/ready
   ```
2. Check the metrics. In the default console exporter, look for:
   - `outbox_dispatch_duration_ms` — histogram of per-message dispatch time.
   - `outbox_dead_lettered_total` — should be `0` or nearly so.
3. Peek at the top of the outbox queue:
   ```sql
   SELECT Id, Topic, Attempts, LastError, NextAttemptAtUtc
   FROM OutboxMessages
   WHERE Dispatched = 0
   ORDER BY NextAttemptAtUtc
   LIMIT 20;
   ```

## Case A — dispatcher is down

The `OutboxDispatcher` is a `BackgroundService`. If the app process is up
but the dispatcher is not making progress, most likely the process
`ExecuteAsync` loop threw an exception outside our `try/catch`. That is a
bug — capture the log and restart the app. In the meantime the queue is
durable; no data is lost.

## Case B — the bus is down / all messages failing

If every message ends up back on the queue with `Attempts++` and a
consistent `LastError`, the downstream bus (RabbitMQ, Azure Service Bus) is
unavailable.

1. If a real broker is unavailable and the bus adapter is a real
   implementation, escalate to the broker's operator.
2. Once the bus is back, dispatch will resume automatically on the next
   poll — no operator action needed.
3. If a subset of messages already hit `MaxAttempts`, they will be in
   `OutboxDeadLetters` — see Case D.

## Case C — dispatcher is fine but the queue keeps growing

The producer rate exceeds the dispatch rate. Options:

- Increase `Outbox:BatchSize`.
- Reduce `Outbox:PollIntervalMilliseconds`.
- Scale out the dispatcher. In a Postgres deployment, use
  `SELECT … FOR UPDATE SKIP LOCKED` in the dispatcher batch query so
  multiple instances can safely poll. On SQLite this is not viable —
  single-writer.

## Case D — dead-lettered messages

Any message that hit `MaxAttempts` is copied to `OutboxDeadLetters` and the
original row is marked `Dispatched = 1` so it leaves the poll set.

1. Inspect:
   ```powershell
   Invoke-RestMethod http://localhost:5001/api/v1/admin/outbox/dead-letters `
       -Headers @{ Authorization = "Bearer $adminToken" }
   ```
2. Decide per-message whether to:
   - Fix the payload manually and re-enqueue by INSERTing a fresh
     `OutboxMessage` (this generates a new `Id`, so subscribers dedup
     correctly against the new id).
   - Drop the message (record why in the audit table).
3. Delete the `OutboxDeadLetter` row when handled.

## Never do

- Never bulk `UPDATE OutboxMessages SET Dispatched = 1` — you lose the
  events downstream is waiting for.
- Never delete an outbox row that has not been dispatched — the event is
  lost. Move it to `OutboxDeadLetters` if you must.

## After-action

- Add a metric alert for `outbox_dead_lettered_total` > 0.
- If the root cause was a subscriber panicking on a specific event shape,
  add a contract test in the subscriber's test suite.
- Consider tuning `MaxAttempts` and the base-backoff to give the subscriber
  more time to recover in the future.
