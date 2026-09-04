# Demo Script

This walkthrough maps to `scripts\demo.ps1`. It is written for a reviewer who wants to see the lakehouse behavior rather than just read the code. The demo uses fictional Contoso Retail data and runs locally with no external infrastructure.

The API is served on `http://localhost:5025`. Commands below use PowerShell. Replace placeholder route segments such as `<sql-query-endpoint>` with the implemented route name in the application. Where a bearer token is required, replace `<reader-jwt>` or `<operator-jwt>` with the project's development JWT for the appropriate role.

## 1. Start from the project folder

```powershell
Set-Location 'C:\Users\rukwaropaul\Downloads\DEV\Projects\25-data-pipeline-lakehouse'
```

What to look for:

- The solution is a .NET 10 clean-architecture project.
- The demo does not require provisioned cloud services.
- SQLite is the serving engine.
- The local lake files are JSONL behind the table-format abstraction.

## 2. Run the narrated demo

```powershell
.\scripts\demo.ps1
```

The script demonstrates the full story:

1. generate synthetic Contoso Retail data;
2. run the 26-task DAG;
3. intentionally break a quality rule;
4. observe the circuit breaker blocking gold promotion;
5. backfill a date range;
6. query gold marts through the SQL API and metrics layer.

Expected seed-run numbers for the supplied generator shape and seed:

- generator: 80 customers, 40 products, 600 orders, 400 sessions, 30 days, seed 42;
- full DAG: 26 tasks;
- `success=true`;
- duration: about 1.3 seconds;
- `totalRowsOut`: about 9,577.

Do not over-explain the dataset as a production business. Contoso Retail is fictional demo data.

## 3. Generate data

The first stage emits synthetic OLTP-style source data and a CDC change feed.

What to look for:

- source records include operations `I`, `U`, and `D`;
- CDC rows carry sequence and commit timestamp values;
- source domains include orders, order lines, customers, products, clickstream, inventory movements, and FX rates;
- currencies include KES, USD, GBP, and EUR.

Why it matters:

- the pipeline is not loading static CSV snapshots only;
- bronze ingestion has enough metadata to prove incremental behavior and replay behavior;
- later SCD2 and late-arriving-dimension cases have source events to exercise.

## 4. Run the normal DAG path

The normal DAG path promotes a window through bronze, silver, gold, aggregates, and serving rebuild.

What to look for in the run output:

- deterministic topological order across 26 tasks;
- per-task timings;
- rows in and rows out;
- retries where applicable;
- OpenTelemetry span creation per task;
- no overlapping run of the same window.

Important behavior to call out:

- bronze is append-only and immutable;
- silver is typed and has a quarantine path;
- bad rows are retained with reasons, not silently dropped;
- silver processing is watermark-driven and idempotent;
- gold facts join the dimension version effective at the event time;
- late or orphan customer references are inferred at gold.

Expected quality-gate result for the seed run:

- silver DQ gate: 14 passed, 1 warn-severity failure;
- silver warning: `referential_integrity:customer_id`, with 24 of 542 orders referencing orphan or late-arriving customers;
- gold DQ gate: 10 passed, 0 failed.

Narration:

> The silver warning is intentional and non-blocking. It keeps the signal visible while allowing gold to conform late-arriving members. Gold referential integrity then passes.

## 5. Inspect the dashboard

Open the local dashboard in a browser after the API is running:

```powershell
Start-Process 'http://localhost:5025'
```

What to look for:

- revenue trend visualization;
- cohort retention heat table;
- funnel view;
- data-quality status indicators.

Real demo values to verify in the dashboard or through mart queries:

- January 2026 revenue by channel, USD:
  - mobile: 81,742.09;
  - partner: 76,050.58;
  - store: 104,075.23;
  - web: 102,948.39;
- `agg_daily_revenue` examples:
  - 2026-01-01: 16 orders, 9,014.45 USD;
  - 2026-01-03: 20 orders, 19,421.97 USD.

## 6. Query gold through the guarded SQL API

Use a read-only query against gold marts. The exact request shape should follow the API's implemented contract; the important review point is that the query is a single `SELECT` or `WITH`, is authenticated, and returns bounded rows from gold serving tables.

Example using `Invoke-RestMethod`:

```powershell
$headers = @{ Authorization = 'Bearer <reader-jwt>' }
$body = @{
  sql = "SELECT channel, ROUND(SUM(revenue_usd), 2) AS revenue_usd FROM agg_daily_revenue WHERE date >= '2026-01-01' AND date < '2026-02-01' GROUP BY channel ORDER BY channel"
  limit = 100
} | ConvertTo-Json

Invoke-RestMethod `
  -Method Post `
  -Uri 'http://localhost:5025/<sql-query-endpoint>' `
  -Headers $headers `
  -ContentType 'application/json' `
  -Body $body
```

Equivalent using `curl.exe`:

```powershell
curl.exe -s -X POST 'http://localhost:5025/<sql-query-endpoint>' `
  -H 'Authorization: Bearer <reader-jwt>' `
  -H 'Content-Type: application/json' `
  -d '{"sql":"SELECT channel, ROUND(SUM(revenue_usd), 2) AS revenue_usd FROM agg_daily_revenue WHERE date >= ''2026-01-01'' AND date < ''2026-02-01'' GROUP BY channel ORDER BY channel","limit":100}'
```

What to look for:

- results come from gold serving tables only;
- no PII is loaded into the serving SQLite database;
- expected January 2026 channel totals are mobile 81,742.09, partner 76,050.58, store 104,075.23, and web 102,948.39.

## 7. Query through the metrics layer

The metrics layer resolves named metrics, allow-listed dimensions, and a closed `TimeGrain` enum into injection-safe SQL.

Example request shape:

```powershell
$headers = @{ Authorization = 'Bearer <reader-jwt>' }
$body = @{
  metric = 'revenue_usd'
  dimensions = @('channel')
  timeGrain = 'month'
  startDate = '2026-01-01'
  endDate = '2026-02-01'
} | ConvertTo-Json

Invoke-RestMethod `
  -Method Post `
  -Uri 'http://localhost:5025/<metrics-query-endpoint>' `
  -Headers $headers `
  -ContentType 'application/json' `
  -Body $body
```

What to look for:

- callers request a metric, not arbitrary string-concatenated SQL;
- dimensions are allow-listed;
- time grain is an enum rather than free text;
- the generated SQL is safe by construction.

## 8. Show SQL guard rejection

Attempt a write or injection-style request. This should return HTTP 400 rather than touching data.

```powershell
$headers = @{ Authorization = 'Bearer <reader-jwt>' }
$body = @{ sql = 'DROP TABLE agg_daily_revenue'; limit = 100 } | ConvertTo-Json

try {
  Invoke-RestMethod `
    -Method Post `
    -Uri 'http://localhost:5025/<sql-query-endpoint>' `
    -Headers $headers `
    -ContentType 'application/json' `
    -Body $body
} catch {
  $_.Exception.Response.StatusCode.value__
}
```

Expected result:

- status code 400;
- no DDL is executed;
- the guard rejects forbidden keywords, batching, comments, non-`SELECT`/`WITH` statements, and write attempts;
- the SQLite connection is read-only.

## 9. Inspect lineage

Column-level lineage is captured from actual transforms and exposed through API endpoints.

Mermaid graph:

```powershell
Invoke-RestMethod `
  -Method Get `
  -Uri 'http://localhost:5025/api/lineage/mermaid' `
  -Headers @{ Authorization = 'Bearer <reader-jwt>' }
```

Upstream lineage:

```powershell
Invoke-RestMethod `
  -Method Get `
  -Uri 'http://localhost:5025/api/lineage/upstream?targetColumn=revenue_usd' `
  -Headers @{ Authorization = 'Bearer <reader-jwt>' }
```

Impact analysis:

```powershell
Invoke-RestMethod `
  -Method Get `
  -Uri 'http://localhost:5025/api/lineage/impact?sourceColumn=orders.customer_id' `
  -Headers @{ Authorization = 'Bearer <reader-jwt>' }
```

What to look for:

- lineage is column-level, not only table-level;
- edges include source columns and transformation descriptions;
- impact analysis answers what breaks if a source column changes;
- the Mermaid graph is generated from persisted lineage metadata.

## 10. Break a quality rule and observe the circuit breaker

`scripts\demo.ps1` intentionally introduces a severe data-quality problem. The expected outcome is not a successful gold promotion; the expected outcome is controlled failure.

What to look for:

- a fail-severity expectation trips the circuit breaker;
- `CircuitBreakerException` is raised;
- downstream gold tasks are marked `Blocked`;
- promotion halts before serving is rebuilt with bad data;
- the DQ report captures the failing expectation and severity.

Narration:

> The point is not that bad data never appears. The point is that critical bad data cannot silently promote into gold.

## 11. Backfill a date range

The script then demonstrates backfill behavior over a date range.

What to look for:

- the DAG can rerun a bounded window;
- the runner computes the task/downstream closure when needed;
- watermarks and deterministic processing prevent duplicate results;
- run history records row counts and timings for the backfill.

Important evidence:

- re-running a window yields identical results;
- checkpoint resume avoids duplication;
- range backfill is covered by tests.

## 12. Close with the engineering summary

End the demo with the three most important implementation points:

1. **Correct dimensional semantics**: facts join the SCD2 dimension version valid at event time.
2. **Governed promotion**: data-quality gates and a circuit breaker prevent critical bad data from reaching gold.
3. **Secure serving boundary**: only gold tables are served; PII remains in bronze/silver files and is not loaded into SQLite.

Honesty notes to state explicitly:

- Contoso Retail is fictional.
- Docker was not verified.
- No cloud infrastructure was provisioned.
- Data files are JSONL, not Parquet, due to the documented .NET 10 `Parquet.Net` untyped read-path issue.

