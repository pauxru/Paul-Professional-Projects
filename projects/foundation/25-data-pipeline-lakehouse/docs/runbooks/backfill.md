# Runbook: Backfill a date range

**When to use:** you need to (re)process a historical window — e.g. a source system replayed corrected
data for last week, a transform bug was fixed and you want history recomputed, or a new source arrived
late for a past period.

## Key facts before you start

- Backfill is a **first-class operation**, not a hack. The DAG runner has a dedicated backfill path that
  executes the DAG per window in order.
- Silver/gold are **deterministic rebuilds from immutable bronze** (ADR-004), so backfilling a window
  **cannot double-count** — re-processing is idempotent by construction.
- Overlapping runs of the same window are rejected (`OverlappingRunException`), so a backfill will not
  collide with a scheduled run of the same window.

## 1. Choose the range

The backfill endpoint takes a date range as query parameters `from` / `to` (`yyyy-MM-dd`, inclusive) and
**internally expands it into one DAG run per day**. Provide the whole corrective range in a single call:

```powershell
$op = (Invoke-RestMethod -Method Post http://localhost:5025/api/auth/token -Body (@{role='operator'}|ConvertTo-Json) -ContentType application/json).token
$h  = @{ Authorization = "Bearer $op" }

Invoke-RestMethod -Method Post "http://localhost:5025/api/pipeline/backfill?from=2026-01-01&to=2026-01-31" -Headers $h
```

## 2. Mind the quarantine baseline over a long range

The silver gate includes a **row-count-anomaly** expectation on the quarantine table that compares each
day's run against a rolling baseline. Because silver is a **deterministic full rebuild from immutable
bronze**, re-processing the same bronze yields the *same* quarantine count each day, so a normal backfill
holds steady and passes. A genuine spike in rejected rows (bad source data landing in the range) can
legitimately trip the anomaly on the offending day — that is the gate working as designed.

Guidance:
- Run the corrective range in **one call** (as above); the deterministic rebuild keeps the quarantine
  volume stable across the per-day runs.
- Watch `/api/quality/latest` after the backfill. If the anomaly tripped, treat it as a **data-quality
  breach** (see that runbook) and decide whether the spike is real (bad source data) or expected.

## 3. Verify the backfill

```powershell
# marts should reflect the reprocessed window
$rd = (Invoke-RestMethod -Method Post http://localhost:5025/api/auth/token -Body (@{role='reader'}|ConvertTo-Json) -ContentType application/json).token
$rh = @{ Authorization = "Bearer $rd" }
Invoke-RestMethod -Method Post http://localhost:5025/api/sql -Headers $rh -ContentType application/json `
  -Body (@{ sql = 'SELECT date_key, orders, revenue_usd FROM agg_daily_revenue ORDER BY date_key' } | ConvertTo-Json)

Invoke-RestMethod http://localhost:5025/api/pipeline/runs -Headers $h | ConvertTo-Json -Depth 5
```

Confirm each backfilled window shows a successful run and the daily revenue rows for the window match
expectations. Because the operation is idempotent, you can safely re-run a window if a verification
check fails.

## Rollback

There is nothing to roll back in the traditional sense: gold is overwritten from silver, which is
rebuilt from immutable bronze. To "undo" a backfill, re-run the DAG for the affected window against the
current bronze — the output is fully determined by bronze state.
