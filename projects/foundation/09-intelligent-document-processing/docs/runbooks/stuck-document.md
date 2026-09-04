# Runbook — Stuck document

A document that is not progressing through the pipeline, or is sitting in an unexpected state.

## Symptoms

- A document stays in `Received`, `Classified`, `Extracted` or `Validated` and never reaches a
  routing decision.
- A document is in `Failed`.
- `GET /api/v1/metrics/stp` shows `processed` lower than `totalDocuments`.

## First checks

1. **Find the document and its history.**
   ```powershell
   $h = @{ Authorization = "Bearer $token" }
   Invoke-RestMethod "http://localhost:5009/api/v1/documents/$id" -Headers $h |
     Select-Object state, routing, documentConfidence
   # transitions show exactly where it stopped and why
   (Invoke-RestMethod "http://localhost:5009/api/v1/documents/$id" -Headers $h).transitions
   ```
2. **Read the last transition `Reason`** — every stage records why it moved (or why it failed).
3. **Check logs by correlation id** — the document's `CorrelationId` ties together all log lines for
   its pipeline run.
4. **Check readiness** — `GET /health/ready` confirms the database is reachable.

## Likely causes and actions

| Cause | Evidence | Action |
| --- | --- | --- |
| Classification could not read the content | stopped at `Classified`/`Failed`, low class confidence | Confirm the upload is a supported format (`.txt/.csv/.json/.ocr.json`); re-upload a clean copy |
| Extraction produced no required fields | stopped at `Extracted`, empty fields | Inspect `/fields`; if a template changed, apply a correction to teach the anchor (ADR-004) |
| Validation threw (bad data) | state `Failed`, exception in logs | Fix the offending field via review correction, then reprocess |
| Object store unreachable | `Failed` early, IO error in logs | Verify `Storage:RootPath` exists and is writable |
| Database not ready | `/health/ready` 503 | Restore DB connectivity, then reprocess |

## Recovery — reprocess

Terminal documents (`Exported`, `Rejected`, `Failed`) can be reprocessed. This resets the document to
`Received` at an incremented version and re-runs the whole pipeline.

```powershell
Invoke-RestMethod -Method Post "http://localhost:5009/api/v1/documents/$id/reprocess" -Headers $h
```

A document stuck in a **non-terminal** state cannot be reprocessed directly (the state machine
forbids it) — resolve the underlying cause first; if it is genuinely wedged, the safe path is to let
the current stage fail (moving it to `Failed`) and then reprocess.

## Escalation / prevention

- Recurring failures from one supplier usually mean a template change → capture a correction so the
  anchor is learned for future documents.
- If many documents fail at the same stage simultaneously, treat it as an infrastructure incident
  (object store or database) rather than a per-document problem.
- Watch the pipeline-stage-duration metric; a stage whose duration spikes is the one to investigate.
