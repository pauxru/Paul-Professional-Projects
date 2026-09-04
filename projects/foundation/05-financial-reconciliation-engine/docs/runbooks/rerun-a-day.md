# Rerun a Day
Re-run reconciliation for a value-date window and verify the run outcome.

## Symptoms
- A corrected import, ruleset activation, or provider update requires a date window to be reconciled again.
- Open exceptions need re-evaluation without duplicating already-known exception records.
- A prior run for the day failed and needs a controlled retry.

## Preconditions / Access
- API is running on port `5005`.
- Base URL is `http://localhost:5005`.
- Operator has a JWT with `recon:run`.
- Use value-date boundaries in `YYYY-MM-DD` format.

Auth preamble:

```bash
BASE_URL="http://localhost:5005"
TOKEN=$(curl -s -X POST "$BASE_URL/api/v1/auth/token" \
  -H "Content-Type: application/json" \
  -d '{"subject":"ops-runner","scopes":["recon:run"]}' \
  | jq -r .accessToken)
```

```powershell
$BaseUrl = "http://localhost:5005"
$Token = (Invoke-RestMethod -Method Post "$BaseUrl/api/v1/auth/token" `
  -ContentType "application/json" `
  -Body '{"subject":"ops-runner","scopes":["recon:run"]}').accessToken
$Headers = @{ Authorization = "Bearer $Token" }
```

## Diagnosis
1. Identify the value-date window to re-run.

   ```bash
   FROM_DATE="2026-09-02"
   TO_DATE="2026-09-02"
   ```

   ```powershell
   $FromDate = "2026-09-02"
   $ToDate = "2026-09-02"
   ```

2. Review recent runs and locate the prior run for comparison.

   ```bash
   curl -s "$BASE_URL/api/v1/runs?page=1&pageSize=20" \
     -H "Authorization: Bearer $TOKEN"
   ```

   ```powershell
   Invoke-RestMethod -Method Get "$BaseUrl/api/v1/runs?page=1&pageSize=20" -Headers $Headers
   ```

3. If a prior run exists, inspect its report and balance report.

   ```bash
   PRIOR_RUN_ID="<prior-run-id>"
   curl -s "$BASE_URL/api/v1/runs/$PRIOR_RUN_ID/report" \
     -H "Authorization: Bearer $TOKEN"
   curl -s "$BASE_URL/api/v1/reports/runs/$PRIOR_RUN_ID/balance" \
     -H "Authorization: Bearer $TOKEN"
   ```

## Resolution
1. Start the re-run for the value-date window.

   ```bash
   RUN_RESPONSE=$(curl -s -X POST "$BASE_URL/api/v1/runs" \
     -H "Authorization: Bearer $TOKEN" \
     -H "Content-Type: application/json" \
     -d "{\"from\":\"$FROM_DATE\",\"to\":\"$TO_DATE\"}")
   echo "$RUN_RESPONSE"
   RUN_ID=$(echo "$RUN_RESPONSE" | jq -r .id)
   ```

   ```powershell
   $Run = Invoke-RestMethod -Method Post "$BaseUrl/api/v1/runs" `
     -Headers $Headers -ContentType "application/json" `
     -Body (@{ from = $FromDate; to = $ToDate } | ConvertTo-Json)
   $RunId = $Run.id
   $Run
   ```

2. If a specific ruleset must be used, include `ruleSetId` in the body:

   ```bash
   curl -s -X POST "$BASE_URL/api/v1/runs" \
     -H "Authorization: Bearer $TOKEN" \
     -H "Content-Type: application/json" \
     -d '{"ruleSetId":"<rule-set-id>","from":"2026-09-02","to":"2026-09-02"}'
   ```

3. Do not manually close duplicate-looking exceptions after a re-run. The orchestrator is idempotent: exceptions are upserted by stable `ExceptionKey`, so existing triaged exceptions are preserved and duplicate exceptions are not created.
4. Expect unmatched records to carry forward and be re-evaluated. Matched records are excluded from future working sets.
5. For identical inputs, compare `InputChecksum` with the prior run. Equality means the run used the same ordered input set.
6. If sums do not reconcile, the run is marked `Failed` and records a `BalanceAssertionException`; escalate before further reruns.

## Verification
1. Check the run status.

   ```bash
   curl -s "$BASE_URL/api/v1/runs/$RUN_ID" \
     -H "Authorization: Bearer $TOKEN"
   ```

   ```powershell
   Invoke-RestMethod -Method Get "$BaseUrl/api/v1/runs/$RunId" -Headers $Headers
   ```

2. Review the run report.

   ```bash
   curl -s "$BASE_URL/api/v1/runs/$RUN_ID/report" \
     -H "Authorization: Bearer $TOKEN"
   ```

   ```powershell
   Invoke-RestMethod -Method Get "$BaseUrl/api/v1/runs/$RunId/report" -Headers $Headers
   ```

3. Verify the balance assertion report.

   ```bash
   curl -s "$BASE_URL/api/v1/reports/runs/$RUN_ID/balance" \
     -H "Authorization: Bearer $TOKEN"
   ```

   ```powershell
   Invoke-RestMethod -Method Get "$BaseUrl/api/v1/reports/runs/$RunId/balance" -Headers $Headers
   ```

4. Confirm:
   - Run status is `Completed`.
   - `BalanceAssertionPassed` is true.
   - No duplicate exceptions were created for the same issue.
   - Existing assigned, resolved, or pending-approval exceptions remain triaged because `ExceptionKey` preserves them.
   - `InputChecksum` matches the prior run only when the inputs are identical.

## Rollback / Escalation
- Reconciliation runs are immutable snapshots; do not edit them directly.
- If the wrong date range was used, start a new run with the correct `{ "from": "YYYY-MM-DD", "to": "YYYY-MM-DD" }`.
- If the run is `Failed` with `BalanceAssertionException`, escalate with:
  - Run ID.
  - Date window.
  - Run report from `/api/v1/runs/{id}/report`.
  - Balance report from `/api/v1/reports/runs/{runId}/balance`.
  - Recent import batch IDs and file checksums for the window.
