# Demo Script

A ~10-minute walkthrough that tells the story: **the value is the guardrails and the measured STP, not
the model.** Run `scripts/demo.ps1` for the automated version, or follow the manual steps below.

## Pre-flight

```powershell
dotnet build -c Release
dotnet run --project src/Idp.Api      # http://localhost:5009, seeds the 19-doc corpus in Development
```

Open a second terminal for the API calls, and a browser at `http://localhost:5009/` for the review
console.

## 0. Framing (30s)

"This is accounts-payable automation. The hard problem isn't reading an invoice — OCR and LLMs do that.
The hard problem is deciding which invoices are safe to pay automatically and routing the rest to a
human with the right evidence. I measure that with a straight-through-processing rate."

## 1. Authenticate (30s)

```powershell
$base = "http://localhost:5009"
$token = (Invoke-RestMethod -Method Post "$base/api/v1/dev/token" `
  -ContentType application/json -Body '{"subject":"demo"}').access_token
$h = @{ Authorization = "Bearer $token" }
```

## 2. The measured KPI first (1 min)

```powershell
Invoke-RestMethod "$base/api/v1/metrics/stp" -Headers $h
```

"Out of 19 seeded documents, 6 processed straight-through (31.58%) and 13 went to review. That low
number is deliberate — the corpus includes degraded documents and three-way-match conditions. A higher
STP would mean weaker guardrails."

## 3. A clean document that auto-approved (2 min)

```powershell
$docs = Invoke-RestMethod "$base/api/v1/documents?page=1&pageSize=50" -Headers $h
$auto = $docs.items | Where-Object routing -eq "AutoApproved" | Select-Object -First 1
Invoke-RestMethod "$base/api/v1/documents/$($auto.id)" -Headers $h |
  Select-Object state, routing, documentConfidence
Invoke-RestMethod "$base/api/v1/documents/$($auto.id)/fields" -Headers $h |
  Select-Object fieldKey, value, confidence, strategy
```

"Every field carries its value, confidence, the strategy that produced it, and a bounding box — the
evidence. High confidence + no hard validation failure → auto-approve."

## 4. A document routed to review, and why (2 min)

```powershell
$q = Invoke-RestMethod "$base/api/v1/review/queue" -Headers $h
$task = $q.items[0]
$doc  = Invoke-RestMethod "$base/api/v1/documents/$($task.documentId)" -Headers $h
$doc.validations | Where-Object outcome -ne "Pass" |
  Select-Object ruleName, outcome, message
```

"The queue is prioritised by value, age and confidence. This one is in review because a validation
rule failed or warned — arithmetic, a date, a duplicate, or the three-way match. The reviewer sees
exactly which fields are implicated."

## 5. Correct it — and show the feedback loop (2 min)

Claim, correct a field against its evidence, approve:

```powershell
Invoke-RestMethod -Method Post "$base/api/v1/review/$($task.taskId)/claim" `
  -Headers $h -ContentType application/json -Body '{"reviewer":"demo"}'
Invoke-RestMethod -Method Post "$base/api/v1/review/$($task.taskId)/correct" `
  -Headers $h -ContentType application/json `
  -Body '{"reviewer":"demo","corrections":[{"fieldKey":"invoiceNumber","newValue":"INV-1001","reason":"OCR misread"}]}'
Invoke-RestMethod -Method Post "$base/api/v1/review/$($task.taskId)/approve" `
  -Headers $h -ContentType application/json -Body '{"reviewer":"demo"}'
```

"The correction is captured with reviewer, reason and timestamp, and it teaches a per-supplier anchor.
The unit test `CorrectionFeedbackTests` proves the next document from that supplier extracts the field
correctly — feedback without retraining a model."

## 6. Export and its guardrails (1 min)

```powershell
Invoke-RestMethod "$base/api/v1/exports" -Headers $h |
  Select-Object documentId, status, attempts, erpReference
```

"Approved documents export to a simulated ERP with bounded retries, an idempotency key so a retry
never double-books, and a dead-letter path when the ERP is down. All re-drivable safely."

## 7. Close (30s)

"Everything you saw runs offline with just the .NET SDK — 125 tests green, classification 100%,
extraction 98.78%, STP measured at 31.58%. The engineering is the guardrails and the honesty of the
number."

## Fallbacks

- If the browser console is not needed, the whole demo runs from the API calls above.
- `scripts/demo.ps1` performs steps 1–6 non-interactively and prints the STP snapshot.
