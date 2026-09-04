# Runbook: Late-arriving and out-of-order data

**When to use:** records for a past period arrive after that period was already processed — late CDC
updates, out-of-order updates (a lower `sequence` seen after a higher one), late-arriving **dimension**
members (a fact references a customer/product not yet in the dimension), or tombstone deletes arriving
after inserts.

## The platform already handles these — this runbook is for confirming/operating that behaviour

### 1. Late-arriving facts

- Bronze is append-only; a late fact is just a new record with its own `commitTs`. Re-processing the
  affected window folds it in — no special handling needed beyond a **backfill** of that window
  (see `backfill.md`).
- Because silver/gold are deterministic rebuilds from immutable bronze (ADR-004), the late fact lands in
  the correct period on re-run with no double counting.

### 2. Out-of-order CDC updates (the classic bug)

- Dedup is by **business key + `sequence`**: the highest-sequence record wins regardless of arrival
  order. An out-of-order (older) update that arrives late does **not** overwrite a newer state.
- SCD2 interval building is **order-independent**: updates are sorted by effective time, so
  `valid_from`/`valid_to`/`is_current` are correct even when updates arrive out of order. This is
  asserted in `Scd2Tests` and `CdcSilverTests`.

### 3. Effective-version join (why late dimension updates don't corrupt facts)

- Facts join to the dimension **version effective at the event time**, not the current version
  (`DimensionResolver.Effective`). So a customer segment change that arrives late does not retroactively
  rewrite the segment on historical order lines — the classic SCD2 fact-join bug is avoided. Asserted in
  `GoldTests` and `DimensionJoinTests`.

### 4. Late-arriving dimension members (inferred members)

- If a fact references a customer/product that isn't in the dimension yet (late or deleted member),
  `GoldBuilder` **infers** a placeholder member (`is_inferred = true`) so the fact key resolves and the
  row is not dropped. When the real member later arrives, a re-run replaces the inferred member with the
  real attributes.

### 5. Tombstone deletes

- CDC `op:D` produces a tombstone. Dedup/merge honour it by business key + sequence so a delete that
  arrives after its insert removes the record from the current state; SCD2 closes the open interval.

## Operating procedure when you learn data arrived late

```powershell
$op = (Invoke-RestMethod -Method Post http://localhost:5025/api/auth/token -Body (@{role='operator'}|ConvertTo-Json) -ContentType application/json).token
$h  = @{ Authorization = "Bearer $op" }

# 1) (re)ingest + reprocess the affected range (from/to expands into per-day windows)
Invoke-RestMethod -Method Post "http://localhost:5025/api/pipeline/backfill?from=2026-01-01&to=2026-01-07" -Headers $h
```

Then verify:

```powershell
$rd = (Invoke-RestMethod -Method Post http://localhost:5025/api/auth/token -Body (@{role='reader'}|ConvertTo-Json) -ContentType application/json).token
$rh = @{ Authorization = "Bearer $rd" }

# inferred members that later got real attributes should no longer be inferred
Invoke-RestMethod -Method Post http://localhost:5025/api/sql -Headers $rh -ContentType application/json `
  -Body (@{ sql = 'SELECT customer_id, is_current, is_inferred, valid_from, valid_to FROM dim_customer ORDER BY customer_id, valid_from' } | ConvertTo-Json) | ConvertTo-Json -Depth 6
```

Confirm the late record is reflected, SCD2 intervals are contiguous and non-overlapping, and no facts
were dropped (row counts stable or higher, never silently lower).

## Watermark note

Bronze watermarks track the high-water mark per entity so normal incremental runs pick up new data
without reprocessing everything. Late data that predates the watermark is handled by an explicit
**backfill** of its window (above), which recomputes the deterministic downstream — the watermark is an
optimisation for the forward path, not a barrier to correcting history.
