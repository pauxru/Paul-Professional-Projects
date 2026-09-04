# Data Quality - real run results

> **Real output.** The tables below are the actual `DataQualityReport`s stored by a seed run
> (`GET /api/quality/latest?gate=silver` and `?gate=gold`), generator = 80 customers / 40 products /
> 600 orders / 400 sessions / 30 days, seed 42. Reproduce with `scripts/demo.ps1`.

## Framework

Expectations are **declarative data**, not code paths (see `QualitySuites.cs`). Each returns an
`ExpectationResult` with a **severity**:

- **Fail** - a blocking failure. The circuit breaker (`CircuitBreaker.Assert`) throws and
  **blocks promotion** to the next medallion layer; every downstream task is marked `Blocked`.
- **Warn** - recorded and surfaced, but promotion continues.

Expectation types implemented: `not_null`, `unique`, `accepted_range`, `accepted_values`,
`referential_integrity`, `freshness`, `row_count_anomaly` (vs a rolling baseline), and
`distribution_drift`. Every type has pass **and** fail unit tests in `ExpectationTests.cs`.

## Silver gate - dataset `silver` (passed 14, failed 1)

| Expectation | Type | Column | Severity | Result | Evaluated | Failed |
|---|---|---|---|---|---:|---:|
| `unique:surrogate_key` | unique | surrogate_key | Fail | PASS | 157 | 0 |
| `not_null:customer_id` | not_null | customer_id | Fail | PASS | 157 | 0 |
| `not_null:valid_from` | not_null | valid_from | Fail | PASS | 157 | 0 |
| `unique:surrogate_key` | unique | surrogate_key | Fail | PASS | 72 | 0 |
| `not_null:product_id` | not_null | product_id | Fail | PASS | 72 | 0 |
| `not_null:order_id` | not_null | order_id | Fail | PASS | 542 | 0 |
| `not_null:customer_id` | not_null | customer_id | Fail | PASS | 542 | 0 |
| `referential_integrity:customer_id` | referential_integrity | customer_id | Warn | **FAIL** | 542 | 24 |
| `unique:order_line_id` | unique | order_line_id | Fail | PASS | 1333 | 0 |
| `range:quantity` | accepted_range | quantity | Fail | PASS | 1333 | 0 |
| `range:net_amount` | accepted_range | net_amount | Fail | PASS | 1333 | 0 |
| `referential_integrity:order_id` | referential_integrity | order_id | Warn | PASS | 1333 | 0 |
| `not_null:rate_to_usd` | not_null | rate_to_usd | Fail | PASS | 120 | 0 |
| `range:rate_to_usd` | accepted_range | rate_to_usd | Fail | PASS | 120 | 0 |
| `row_count_anomaly` | row_count_anomaly | * | Fail | PASS | 207 | 0 |

**Reading the one FAIL.** `referential_integrity:customer_id` is **Warn** severity and failed on
24 of 542 orders. These reference customers that were **deleted (tombstoned) or rejected** from the
conformed SCD2 dimension - legitimate late-arriving/deleted members. Gold resolves them by
**inferring** a dimension member (`GoldBuilder` -> `InferCustomer`), so they must not block the
pipeline. The **blocking** signal at silver is instead `row_count_anomaly` on the quarantine table:
if reject volume balloons past the rolling baseline, that is a real incident and the gate fails.

## Gold gate - dataset `gold` (passed 10, failed 0)

| Expectation | Type | Column | Severity | Result | Evaluated | Failed |
|---|---|---|---|---|---:|---:|
| `unique:customer_sk` | unique | customer_sk | Fail | PASS | 235 | 0 |
| `not_null:customer_id` | not_null | customer_id | Fail | PASS | 235 | 0 |
| `unique:product_sk` | unique | product_sk | Fail | PASS | 107 | 0 |
| `unique:order_line_id` | unique | order_line_id | Fail | PASS | 1333 | 0 |
| `not_null:order_line_id` | not_null | order_line_id | Fail | PASS | 1333 | 0 |
| `range:net_amount_usd` | accepted_range | net_amount_usd | Fail | PASS | 1333 | 0 |
| `referential_integrity:customer_sk` | referential_integrity | customer_sk | Fail | PASS | 1333 | 0 |
| `referential_integrity:product_sk` | referential_integrity | product_sk | Fail | PASS | 1333 | 0 |
| `referential_integrity:order_date_key` | referential_integrity | order_date_key | Fail | PASS | 1333 | 0 |
| `unique:session_id` | unique | session_id | Fail | PASS | 400 | 0 |

Gold referential integrity passes because inferred members were merged into `dim_customer` /
`dim_product` during the fact build, so every `customer_sk` / `product_sk` / `order_date_key`
resolves. This is the whole point of the Warn-at-silver / conform-at-gold policy (see ADR-003).

## Quarantine

Bad rows are **never silently dropped** - `SilverBuilder` writes each reject to the `quarantine`
table with the `reason`, `source_table`, `run_id` and the raw `payload`. The quarantine slice
for a source is replaced (delete-by-source then append) on every run, so re-running never accumulates
duplicates. Reasons observed in this run include: null/blank required fields, negative amounts,
non-numeric prices, bad dates, orphan FKs and discount-exceeds-gross.

