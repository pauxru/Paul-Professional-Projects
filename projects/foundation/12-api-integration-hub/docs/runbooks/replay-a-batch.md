# Runbook — Replay a Batch

## Preconditions

- The underlying connector or data issue is corrected.
- The operator has `hub.write`.
- The original target supports the connector's idempotency contract.

## Inspect

```powershell
$items = Invoke-RestMethod `
  -Uri "http://localhost:5012/api/v1/dead-letters?status=Pending" `
  -Headers $headers
$batch = $items | Where-Object batchKey -eq "crm-to-erp"
```

Review redacted payload shape, error class, target health, and contract-drift alerts.

## Replay one record first

```powershell
Invoke-RestMethod -Method Post `
  -Uri "http://localhost:5012/api/v1/dead-letters/replay" `
  -Headers $headers -ContentType application/json `
  -Body (@{ itemId = $batch[0].id } | ConvertTo-Json)
```

Confirm the replay run succeeded and the target did not create a duplicate.

## Replay the batch

```powershell
Invoke-RestMethod -Method Post `
  -Uri "http://localhost:5012/api/v1/dead-letters/replay" `
  -Headers $headers -ContentType application/json `
  -Body (@{ batchKey = "crm-to-erp" } | ConvertTo-Json)
```

## Verification

- Pending DLQ depth decreased by the expected count.
- Replay run has `Succeeded` or an explained `PartiallySucceeded`.
- Target resource count matches distinct business keys.
- Repeating the same replay selector produces no second target write.
