# Runbook — Usage Backfill

## Purpose

Safely ingest delayed metering data without duplicating events or rewriting closed financial history.

## Prepare

1. Identify subscription, meter, occurrence period, source event IDs, expected aggregation mode, and source-of-truth checksum.
2. Confirm every source record has a stable unique `eventId`. Never generate a new ID for the same source record on retry.
3. Determine whether the containing billing period is open or closed.
4. Confirm configured closed-period behavior:
   - `Reject`: closed events return a domain rejection.
   - `CreditNextOpenPeriod`: evidence retains original occurrence time but contributes to the current open rollup.
5. For corrections, post a new event with `adjustmentOfEventId`; never update/delete the original.

## Execute

```powershell
$body = @{
  eventId = "backfill-source-2026-08-31-00042"
  subscriptionId = "<subscription-guid>"
  meterId = "<meter-guid>"
  occurredAt = "2026-08-31T23:58:00Z"
  quantity = 125
  uniqueKey = $null
  adjustmentOfEventId = $null
} | ConvertTo-Json

Invoke-RestMethod -Method Post -Uri "http://localhost:5016/api/v1/usage" `
  -Headers @{ Authorization = "Bearer <token>" } `
  -ContentType "application/json" -Body $body
```

Throttle batches to protect the shared database and retain source-to-response logs outside the application repository.

## Verify

1. Re-send a sample event ID and confirm `"duplicate": true` with no aggregate change.
2. Count inserted immutable events against accepted source IDs.
3. Verify one rollup per subscription/meter/period and expected aggregate:
   - sum: algebraic quantity total,
   - max: peak,
   - last-value: latest timestamp then event ID,
   - unique-count: distinct unique keys.
4. Verify billable units after configured increment/mode.
5. If credited forward, reconcile the next invoice's usage line and retain the original occurrence period in audit evidence.

## Rollback

There is no destructive rollback. Post explicit adjustment events that reverse incorrect quantities or add compensating account credit/credit notes after invoicing. Document source IDs and reason.
