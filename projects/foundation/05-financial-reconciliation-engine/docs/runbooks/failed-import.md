# Failed Import
Diagnose and recover from an import that returns HTTP 400 or many rejected rows.

## Symptoms
- `POST /api/v1/imports` returns HTTP 400 ProblemDetails.
- Import completes, but `RejectedRows` is unexpectedly high.
- Downstream reconciliation has missing records because accepted rows are lower than expected.

## Preconditions / Access
- API is running on port `5005`.
- Base URL is `http://localhost:5005`.
- Operator has a JWT bearer token. Imports require authentication.
- Use the correct built-in profile: `internal-csv`, `external-csv`, or `external-fixed`.

Auth preamble:

```bash
BASE_URL="http://localhost:5005"
TOKEN=$(curl -s -X POST "$BASE_URL/api/v1/auth/token" \
  -H "Content-Type: application/json" \
  -d '{"subject":"ops-import","scopes":["recon:run","recon:resolve","recon:approve"]}' \
  | jq -r .accessToken)
```

```powershell
$BaseUrl = "http://localhost:5005"
$Token = (Invoke-RestMethod -Method Post "$BaseUrl/api/v1/auth/token" `
  -ContentType "application/json" `
  -Body '{"subject":"ops-import","scopes":["recon:run","recon:resolve","recon:approve"]}').accessToken
$Headers = @{ Authorization = "Bearer $Token" }
```

## Diagnosis
1. Confirm the import batch summary.

   ```bash
   IMPORT_ID="<import-batch-id>"
   curl -s "$BASE_URL/api/v1/imports/$IMPORT_ID" \
     -H "Authorization: Bearer $TOKEN"
   ```

   ```powershell
   $ImportId = "<import-batch-id>"
   Invoke-RestMethod -Method Get "$BaseUrl/api/v1/imports/$ImportId" -Headers $Headers
   ```

2. Retrieve rejected rows. This is the rejected-rows report: it includes line numbers, reasons, and raw lines.

   ```bash
   curl -s "$BASE_URL/api/v1/imports/$IMPORT_ID/rejections" \
     -H "Authorization: Bearer $TOKEN"
   ```

   ```powershell
   Invoke-RestMethod -Method Get "$BaseUrl/api/v1/imports/$ImportId/rejections" -Headers $Headers
   ```

3. Check the rejection reasons against common causes:
   - Wrong `profile`: `internal-csv`, `external-csv`, or `external-fixed`.
   - Bad date formats for the selected profile.
   - Decimal separator mismatch.
   - Wrong amount sign for internal vs external data.
   - Minor-vs-major unit mismatch.
   - Malformed CSV, especially unbalanced quotes.
   - Empty file.

Note: ingestion is streaming. Row-level failures are captured as `ImportRejection`; one bad row does not automatically reject the whole file.

## Resolution
1. If the wrong profile was used, re-import the original file with the correct `profile`.
2. If row content is invalid, fix the source file lines reported by `/rejections`.
3. Preserve the intended currency, amount sign, and date semantics when correcting rows.
4. Re-import the corrected file:

   ```bash
   curl -s -X POST "$BASE_URL/api/v1/imports" \
     -H "Authorization: Bearer $TOKEN" \
     -F "profile=internal-csv" \
     -F "file=@C:\path\to\corrected-file.csv"
   ```

   ```powershell
   $Form = @{
     profile = "internal-csv"
     file = Get-Item "C:\path\to\corrected-file.csv"
   }
   Invoke-RestMethod -Method Post "$BaseUrl/api/v1/imports" -Headers $Headers -Form $Form
   ```

5. Record the new `BatchId`, `AcceptedRows`, `RejectedRows`, and `FileChecksum`.

## Verification
- `GET /api/v1/imports/{id}` shows the expected `AcceptedRows` and a reduced or zero `RejectedRows`.
- `GET /api/v1/imports/{id}/rejections` contains no unexpected reasons.
- If re-importing was a prerequisite for reconciliation, the next run includes the expected records.

## Rollback / Escalation
- There is no destructive rollback for an import batch in the public API. Do not edit the database manually.
- If the corrected file still rejects, escalate with:
  - Import `BatchId`.
  - File name and selected `profile`.
  - `FileChecksum`.
  - Rejection report from `/api/v1/imports/{id}/rejections`.
  - Correlation ID from the failed request, if available.
