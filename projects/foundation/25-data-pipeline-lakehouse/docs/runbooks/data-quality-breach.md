# Runbook: Data-quality breach (circuit breaker tripped)

**Symptom:** `dq_silver` or `dq_gold` task fails with a `CircuitBreakerException`; gold tasks show as
`Blocked`; the marts were **not** promoted. This is the platform protecting downstream consumers — the
gate did its job.

## 1. Read the data-quality report

```powershell
$rd = (Invoke-RestMethod -Method Post http://localhost:5025/api/auth/token -Body (@{role='reader'}|ConvertTo-Json) -ContentType application/json).token
$rh = @{ Authorization = "Bearer $rd" }

# latest report (silver + gold), with each expectation, severity, and failed count
Invoke-RestMethod http://localhost:5025/api/quality/latest -Headers $rh | ConvertTo-Json -Depth 8
```

Each result has `Name`, `Column`, `Severity` (`Warn`=0 / `Fail`=1), `Passed`, `FailedCount`, and a
`Message`. Only a **blocking** (`Fail`, not passed) result trips the breaker.

## 2. Identify which expectation blocked

Common blocking signals in this platform:

- **Quarantine row-count anomaly (`Fail`)** — the volume of rejected rows jumped versus the rolling
  baseline. Something upstream got worse: a surge of nulls in required fields, negative amounts, bad
  dates, or invalid FKs.
- **A gold not-null / accepted-range / uniqueness `Fail`** — a curated invariant was violated (e.g. a
  surrogate key collision, a negative `net_amount_usd`).

> Note on referential integrity: silver RI expectations on `customer_id` / `order_id` are **`Warn`**, not
> `Fail`, on purpose (ADR-003) — the generator intentionally produces orphan FKs, and gold **infers**
> those late/deleted members rather than dropping facts. So an RI warning alone does **not** block; a
> quarantine anomaly or a gold hard-invariant does.

## 3. Triage the quarantined rows

Rejected rows are written to the `quarantine` table on the lake with the **reason** and the raw payload.
Inspect them on the filesystem lake (they are deliberately **not** exposed through the query API — see
the security review). Group by reason to see the dominant defect:

- If the spike is a **real upstream problem** (source system emitting bad data) → fix at source /
  raise with the data producer; keep the gate closed until resolved.
- If it is **expected** (a known one-off, e.g. a big legitimate load) → after review, an operator may
  re-run; the baseline will absorb the new normal on subsequent runs.

## 4. Recover

Once the root cause is addressed (corrected source data re-ingested, or the spike accepted as the new
baseline):

```powershell
$op = (Invoke-RestMethod -Method Post http://localhost:5025/api/auth/token -Body (@{role='operator'}|ConvertTo-Json) -ContentType application/json).token
$oh = @{ Authorization = "Bearer $op" }

# re-run the pipeline for the window; deterministic + idempotent (ADR-004)
Invoke-RestMethod -Method Post "http://localhost:5025/api/pipeline/backfill?from=2026-01-01&to=2026-01-31" -Headers $oh
```

## 5. Verify the gate is green

```powershell
Invoke-RestMethod http://localhost:5025/api/quality/latest -Headers $rh | ConvertTo-Json -Depth 6
Invoke-RestMethod http://localhost:5025/api/dashboard/quality | ConvertTo-Json -Depth 6   # anon
```

Confirm no blocking failures remain and gold tasks now `Succeeded`. The dashboard quality tile should
show green.

## Do NOT

- **Do not** widen a `Fail` expectation to `Warn` just to make a run pass. Changing severity is a
  deliberate policy decision (ADR-003), reviewed like code — not an incident workaround.
- **Do not** hand-edit gold tables to "fix" data. Fix upstream and re-run; gold is derived, not
  authoritative.
