# Runbook — Export failures

Approved documents that fail to export to the ERP, or that land in the dead-letter queue.

## Symptoms

- `GET /api/v1/exports` shows records in `Failed` or `DeadLettered`.
- A document is `AutoApproved`/`Corrected` but never becomes `Exported`.
- The dead-letter folder is accumulating files.

## Background — how export behaves

`ExportService` attempts an export while the record `CanAttempt` (status `Pending`/`Failed` and
`Attempts < MaxAttempts`, default **3**):

- **Transient failure** → `MarkAttemptFailed`; when attempts reach the max the record becomes
  `DeadLettered`.
- **Permanent (non-transient) failure** → `ForceDeadLetter` immediately, no further retries.
- **Success** → writes the ERP payload to the **outbox** folder, marks `Succeeded`, records the ERP
  reference, and moves the document to `Exported`.
- **Dead-letter** → the payload is written to the **dead-letter** folder for operator attention.

An **idempotency key** (`{documentId}:v{version}`) guarantees that a retry after an ambiguous ERP
response never double-books — the simulated ERP returns the original reference for a repeated key.

## First checks

```powershell
$h = @{ Authorization = "Bearer $token" }   # needs export:manage
Invoke-RestMethod "http://localhost:5009/api/v1/exports" -Headers $h |
  Select-Object documentId, status, attempts, maxAttempts, lastError
```

1. Read `lastError` on the failing record — it distinguishes transient from permanent causes.
2. Inspect the **dead-letter folder** (`Export:DeadLetterPath`) for the payload.
3. Confirm the ERP endpoint configuration (`Export:ErpEndpoint`) if a real HTTP adapter is wired.

## Likely causes and actions

| Cause | Evidence | Action |
| --- | --- | --- |
| Transient ERP/network blip | `lastError` transient, attempts < max | Re-export; it will retry |
| Exhausted retries | status `DeadLettered`, attempts = max | Fix root cause, then re-export |
| Permanent rejection (bad payload) | `lastError` non-transient | Correct the document via review, reprocess, then re-export |
| Document not in an exportable state | export skipped | Ensure document is `AutoApproved`/`InReview`/`Corrected` before export |
| Outbox/dead-letter path not writable | IO error in `lastError` | Fix `Export:OutboxPath`/`DeadLetterPath` permissions |

## Recovery — re-export

Re-export resets the record for another attempt (respecting idempotency, so no double-booking):

```powershell
Invoke-RestMethod -Method Post `
  "http://localhost:5009/api/v1/exports/$documentId/reexport" -Headers $h
```

For a **batch** of dead-lettered records, iterate the export list and re-export each after the root
cause is resolved.

## Escalation / prevention

- A sudden rise in dead-letters usually means the ERP (or its network path) is down — pause
  re-export attempts until it recovers, then drain the dead-letter queue.
- Alert on dead-letter folder growth and on `DeadLettered` count from `GET /api/v1/exports`.
- Because exports are idempotent, re-driving the dead-letter queue is safe once the ERP is healthy.
