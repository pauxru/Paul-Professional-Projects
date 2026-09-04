# Runbook — DLQ drain

## Symptoms

- `GET /api/v1/admin/dlq` returns > 0 items.
- Ops dashboard shows `DLQ depth: <n>`.

## Assess

1. `GET /api/v1/admin/dlq?page=1&pageSize=50` — list.
2. Inspect a few items; look at `FailureReason` and `Attempts`.
3. Group by `templateKey` and `provider` if the API supports it, else pull
   the raw rows from the DB:
   ```sql
   SELECT TemplateKey, ProviderName, COUNT(*)
   FROM notifications
   WHERE Status = 10 -- DeadLettered
   GROUP BY TemplateKey, ProviderName;
   ```

## Decide

- If the underlying cause is fixed (provider outage, template bug,
  authentication misconfig), replay is safe.
- If the cause is unresolved, replaying will just re-DLQ. Fix first.

## Replay

```powershell
$body = @{ ids = @("<guid>", "<guid>") } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri http://localhost:5011/api/v1/admin/dlq/replay `
    -Headers @{ Authorization = "Bearer $token" } `
    -ContentType 'application/json' -Body $body
```

Response:

```json
{ "replayed": 2, "skipped": 0 }
```

Replayed notifications transition back to `Queued`; the pipeline picks
them up on the next tick.

## Follow up

- Add a DLQ alarm at ≥ 50 items to prometheus.
- Consider a scheduled auto-replay window for known-transient failures.
