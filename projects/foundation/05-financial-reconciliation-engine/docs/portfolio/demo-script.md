# Live Demo Script — Financial Reconciliation & Settlement Engine

Target length: **5–10 minutes**. This narration complements any automation script by explaining what the audience is seeing and why it matters.

## 0. Setup

Narration: "This is a self-directed .NET 10 case study. The API runs locally on SQLite, so there is no external infrastructure required. The demo data is fictional and deterministic."

```powershell
cd C:\Users\rukwaropaul\Downloads\DEV\Projects\05-financial-reconciliation-engine
```

Optional confidence check:

```powershell
dotnet build -c Release
dotnet test -c Release
```

Expected proof point from the verified run: 0 build warnings/errors and 118 passing tests.

## 1. Generate deterministic demo data

Narration: "First I generate a synthetic settlement dataset. The generator emits internal ledger CSV, external provider CSV, external fixed-width data, and a manifest containing the expected defect counts."

```powershell
dotnet run --project src\ReconEngine.DataGen -- --rows 250000 --seed 42 --inject duplicates,amount-mismatch,missing,fees,refunds --out .\data
Get-Content .\data\manifest.json
```

Point out: the files are fictional; the manifest is the ground truth used to test exact defect counts.

## 2. Start the API on port 5005

Narration: "The API is a minimal API with JWT auth, ProblemDetails, correlation IDs, rate limiting, Serilog, and OpenTelemetry instrumentation."

```powershell
$env:ASPNETCORE_URLS = 'http://localhost:5005'
dotnet run --project src\ReconEngine.Api
```

Use a second terminal for the remaining commands:

```powershell
$BASE = 'http://localhost:5005/api/v1'
curl.exe -s http://localhost:5005/health
curl.exe -s http://localhost:5005/health/ready
```

## 3. Get JWT tokens

Narration: "Demo tokens are issued by the local API. The main operator can run reconciliations and resolve exceptions. A separate approver token demonstrates four-eyes control."

```powershell
$operatorToken = (curl.exe -s -X POST "$BASE/auth/token" `
  -H 'Content-Type: application/json' `
  -d '{ "subject": "demo-operator", "scopes": ["recon:run", "recon:resolve", "recon:approve"] }' | ConvertFrom-Json).accessToken

$approverToken = (curl.exe -s -X POST "$BASE/auth/token" `
  -H 'Content-Type: application/json' `
  -d '{ "subject": "demo-approver", "scopes": ["recon:approve"] }' | ConvertFrom-Json).accessToken
```

## 4. Import internal and external files

Narration: "Imports are streaming. Each accepted row is normalized and hashed; bad rows are captured as import rejections instead of crashing the batch."

```powershell
curl.exe -s -X POST "$BASE/imports" `
  -H "Authorization: Bearer $operatorToken" `
  -F "file=@.\data\internal.csv" `
  -F "profile=internal-csv"

curl.exe -s -X POST "$BASE/imports" `
  -H "Authorization: Bearer $operatorToken" `
  -F "file=@.\data\external.csv" `
  -F "profile=external-csv"
```

To demonstrate fixed-width support instead of the external CSV path, import the generated fixed-width file with the fixed-width profile:

```powershell
curl.exe -s -X POST "$BASE/imports" `
  -H "Authorization: Bearer $operatorToken" `
  -F "file=@.\data\external.fixed.txt" `
  -F "profile=external-fixed"
```

Useful import routes:

```powershell
curl.exe -s -H "Authorization: Bearer $operatorToken" "$BASE/imports?page=1&pageSize=10"
curl.exe -s -H "Authorization: Bearer $operatorToken" "$BASE/imports/{id}/rejections"
```

## 5. Start a reconciliation run

Narration: "A run snapshots the active ruleset version, loads only non-matched records, performs matching, classifies exceptions, persists an immutable run, and asserts the balance."

```powershell
$run = curl.exe -s -X POST "$BASE/runs" `
  -H "Authorization: Bearer $operatorToken" `
  -H 'Content-Type: application/json' `
  -d '{}' | ConvertFrom-Json

$RUN_ID = $run.id
$RUN_ID
```

## 6. View the run report and balance assertion

Narration: "The run report is the executive view: input counts, matches, exceptions, carried-forward records, totals, and the balance assertion. The balance endpoint proves the engine self-checks per currency."

```powershell
curl.exe -s -H "Authorization: Bearer $operatorToken" "$BASE/runs/$RUN_ID"
curl.exe -s -H "Authorization: Bearer $operatorToken" "$BASE/runs/$RUN_ID/report"
curl.exe -s -H "Authorization: Bearer $operatorToken" "$BASE/reports/runs/$RUN_ID/summary"
curl.exe -s -H "Authorization: Bearer $operatorToken" "$BASE/reports/runs/$RUN_ID/balance"
```

## 7. List exceptions and aging

Narration: "Unmatched or flagged items become durable exceptions. Re-runs preserve existing triage through stable exception keys instead of creating duplicates."

```powershell
curl.exe -s -H "Authorization: Bearer $operatorToken" "$BASE/exceptions?status=Open&page=1&pageSize=10"
curl.exe -s -H "Authorization: Bearer $operatorToken" "$BASE/reports/aging?format=json"
curl.exe -s -H "Authorization: Bearer $operatorToken" "$BASE/reports/aging?format=csv"
```

## 8. Resolve one exception with four-eyes approval

Narration: "For write-offs at or above KES 1,000.00, the maker cannot approve their own proposal. The exception moves to PendingApproval and a different user must approve or reject it. Every transition appends an audit entry."

Select an exception that requires write-off approval:

```powershell
$exceptions = curl.exe -s -H "Authorization: Bearer $operatorToken" "$BASE/exceptions?status=Open&page=1&pageSize=100" | ConvertFrom-Json
$exception = $exceptions.items | Where-Object { [Math]::Abs($_.amountMinor) -ge 100000 } | Select-Object -First 1
$EXCEPTION_ID = $exception.id
$EXCEPTION_ID
```

Assign it:

```powershell
curl.exe -s -X POST "$BASE/exceptions/$EXCEPTION_ID/assign" `
  -H "Authorization: Bearer $operatorToken" `
  -H 'Content-Type: application/json' `
  -d '{ "assignee": "demo-operator" }'
```

Propose a write-off resolution:

```powershell
curl.exe -s -X POST "$BASE/exceptions/$EXCEPTION_ID/resolve" `
  -H "Authorization: Bearer $operatorToken" `
  -H 'Content-Type: application/json' `
  -d '{ "reason": "WriteOff", "note": "Demo write-off proposed by maker." }'
```

Approve with a different user:

```powershell
curl.exe -s -X POST "$BASE/exceptions/$EXCEPTION_ID/approve" `
  -H "Authorization: Bearer $approverToken"

curl.exe -s -H "Authorization: Bearer $operatorToken" "$BASE/exceptions/$EXCEPTION_ID"
```

## 9. Show OpenAPI

Narration: "The API is discoverable through its OpenAPI document. Use this to show the endpoint surface: imports, rulesets, runs, exceptions, reports, and health checks."

Open in a browser:

```powershell
Start-Process 'http://localhost:5005/openapi'
```

Fetch the OpenAPI document directly:

```powershell
curl.exe -s http://localhost:5005/openapi/v1.json
```

## 10. Close

Narration: "The key portfolio point is not that this is connected to a real bank; it is not. The point is that it demonstrates the engineering controls expected in financial reconciliation: deterministic ingestion, auditable matching, durable exceptions, four-eyes approval, idempotent re-runs, and measurable performance."
