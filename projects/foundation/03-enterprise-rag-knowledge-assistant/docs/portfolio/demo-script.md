# Demo script — five-minute walk-through

Goal: convince a reviewer in five minutes that this is a real system with
enforceable guarantees, not a slide deck.

## Setup (30 s)

```powershell
cd 03-enterprise-rag-knowledge-assistant
dotnet build -c Release
dotnet run --project src\RagAssistant.Api -c Release
```

Second terminal:

```powershell
$emp    = (curl -s -X POST http://localhost:5003/api/v1/auth/token -H "Content-Type: application/json" -d '{"userId":"alice","roles":["employee"],"departments":["hr"],"classification":"Internal"}').token
$board  = (curl -s -X POST http://localhost:5003/api/v1/auth/token -H "Content-Type: application/json" -d '{"userId":"bob","roles":["employee","board","admin"],"departments":[],"classification":"Restricted"}').token
```

## 1. Grounded answer with citations (60 s)

```powershell
curl -s -X POST http://localhost:5003/api/v1/query `
  -H "Authorization: Bearer $emp" -H "Content-Type: application/json" `
  -d '{"query":"How many paid time off days do employees receive?","mode":"Hybrid","topK":4}' | jq
```

Point out:

- `answer` contains inline `[1]` citation markers.
- `citations[]` has document title, chunk id, char span, score.
- `refused` = false, `supportRatio` > 0.4, `promptVersion` recorded.

## 2. Refusal below threshold (30 s)

```powershell
curl -s -X POST http://localhost:5003/api/v1/query `
  -H "Authorization: Bearer $emp" -H "Content-Type: application/json" `
  -d '{"query":"What is the capital of Mars?","mode":"Hybrid","topK":4}' | jq
```

Point out `refused=true`, `refusalReason=insufficient-retrieval`, and a
polite refusal message rather than a hallucination.

## 3. Permission leak test (60 s)

Same restricted question, two identities:

```powershell
# As employee — should NOT see the LTIP figure
curl -s -X POST http://localhost:5003/api/v1/query -H "Authorization: Bearer $emp" -H "Content-Type: application/json" -d '{"query":"What is the CEO LTIP pool for the current fiscal year?","mode":"Hybrid","topK":4}' | jq

# As board member — sees the restricted document listed
curl -s -X GET "http://localhost:5003/api/v1/documents?pageSize=100" -H "Authorization: Bearer $board" | jq '.[] | select(.classification=="Restricted")'
```

Point out: the employee's citations never include the board-only document,
and the substring "LTIP" / "3.5 million" never appears in the answer.

## 4. Evaluation harness (60 s)

```powershell
dotnet run --project src\RagAssistant.Eval -c Release
```

Read out the three modes' Recall@5 / MRR / nDCG, and the citation precision
and refusal accuracy. Reference `docs/evaluation.md` for the honest
interpretation.

## 5. Prompt versioning (30 s)

```powershell
curl -s -X POST http://localhost:5003/api/v1/admin/prompts `
  -H "Authorization: Bearer $board" -H "Content-Type: application/json" `
  -d '{"name":"rag.answer","version":"demo-v2","body":"..."}' | jq
```

Ask another query; show the new `promptVersion` in the response.

## 6. Ops surface (30 s)

- `GET /health/live`, `/health/ready`
- OpenTelemetry console exporter — point out the spans and metrics being
  emitted while the demo runs.
- ProblemDetails errors — POST an empty body and show the RFC 9457 shape.
