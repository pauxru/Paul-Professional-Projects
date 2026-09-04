# Runbook: Failed pipeline run

**Symptom:** A DAG run reports failure, or `Success = false` in run history, or an API `run`/`rerun`
call returns non-2xx.

## 1. Triage — find the failing task

```powershell
# operator token
$op = (Invoke-RestMethod -Method Post http://localhost:5025/api/auth/token -Body (@{role='operator'}|ConvertTo-Json) -ContentType application/json).token
$h  = @{ Authorization = "Bearer $op" }

# most recent runs, with per-task state/timings/row counts
Invoke-RestMethod http://localhost:5025/api/pipeline/runs -Headers $h | ConvertTo-Json -Depth 6
```

Look for the task whose `State` is `Failed` (the first one). Tasks after it that are `Blocked` are
*downstream victims*, not the root cause — ignore them until the root cause is fixed.

## 2. Classify the failure

- **`CircuitBreakerException` on `dq_silver` / `dq_gold`** → this is a **data-quality breach**, not a
  code failure. The gate did its job and blocked promotion. Go to `data-quality-breach.md`.
- **Transient error** (I/O, momentary lock) → tasks retry automatically up to their `maxRetries`; if
  attempts were exhausted, a simple re-run usually clears it.
- **`OverlappingRunException` (409)** → another run for the same window is in progress. Wait for it to
  finish; do not force a concurrent run.
- **Deterministic exception in a transform** → a real bug or bad input; inspect structured logs filtered
  by `RunId` + `TaskId`.

## 3. Recover — partial re-run of only the failed task and its downstream

You do **not** need to re-run the whole pipeline. Re-run the failed task; the runner recomputes its
downstream closure only (`?task=` is a single task id):

```powershell
Invoke-RestMethod -Method Post "http://localhost:5025/api/pipeline/rerun?task=silver_orders" -Headers $h
```

Because silver/gold are deterministic rebuilds from immutable bronze (ADR-004), re-running is **safe and
idempotent** — it cannot double-count or corrupt state.

## 4. Verify

```powershell
Invoke-RestMethod http://localhost:5025/api/observability/freshness -Headers $h | ConvertTo-Json -Depth 4
Invoke-RestMethod http://localhost:5025/api/quality/latest       -Headers $h | ConvertTo-Json -Depth 6
```

Confirm the previously-failed task is `Succeeded`, freshness for the affected tables is current, and the
DQ report has no blocking failures. Close the incident.

## Escalation / notes

- If the same deterministic failure recurs after a re-run, it is a code or data-contract bug — capture
  the `RunId`, the task id, and the structured-log excerpt, and fix forward (add/adjust a test).
- Bronze is immutable: a failed run never leaves partial bronze data because commits are atomic (a run
  either commits a snapshot or it does not).
