# Screenshots Needed

Capture these screenshots after running `scripts\demo.ps1` and starting the API on `http://localhost:5025`. The goal is to show concrete behavior: marts, quality gates, lineage, orchestration, and SQL security.

## Dashboard: revenue trend bars

- **URL / endpoint**: `http://localhost:5025`
- **Capture**: revenue trend section of the dependency-free HTML/JS dashboard.
- **Demonstrates**:
  - gold aggregate marts are being served through SQLite;
  - the dashboard is generated without a frontend dependency stack;
  - revenue is normalized to USD through the FX dimension.
- **Numbers to confirm where visible**:
  - January 2026 channel revenue, USD: mobile 81,742.09; partner 76,050.58; store 104,075.23; web 102,948.39;
  - daily samples: 2026-01-01 has 16 orders and 9,014.45 USD; 2026-01-03 has 20 orders and 19,421.97 USD.

## Dashboard: cohort retention heat table

- **URL / endpoint**: `http://localhost:5025`
- **Capture**: cohort retention heat table.
- **Demonstrates**:
  - `agg_cohort_retention` is built as an incremental gold mart;
  - the serving layer can expose analytical tables, not only raw facts;
  - the project supports product-analytics-style outputs from local lakehouse processing.

## Dashboard: funnel

- **URL / endpoint**: `http://localhost:5025`
- **Capture**: funnel visualization.
- **Demonstrates**:
  - `agg_funnel` is populated from clickstream sessions;
  - clickstream data flows through the same bronze, silver, gold, and serving boundary as transactional data;
  - the dashboard can combine multiple analytical mart types.

## Dashboard: data-quality status pills

- **URL / endpoint**: `http://localhost:5025`
- **Capture**: DQ status area showing silver and gold gate outcomes.
- **Demonstrates**:
  - declarative data-quality expectations are evaluated per run;
  - warn/fail severity is visible to an operator;
  - silver can warn while gold passes after conformance.
- **Expected seed-run values**:
  - silver DQ gate: 14 passed, 1 warn-severity failure;
  - warning: `referential_integrity:customer_id`, 24 of 542 orders were orphan or late-arriving customers;
  - gold DQ gate: 10 passed, 0 failed.

## Lineage Mermaid graph

- **URL / endpoint**: `http://localhost:5025/api/lineage/mermaid`
- **Capture**: rendered Mermaid graph, or the endpoint output plus rendered preview.
- **Demonstrates**:
  - lineage is captured automatically from actual transforms;
  - lineage is column-level, not merely table-level;
  - transformations include source columns and target columns;
  - the graph is queryable and renderable.

## Lineage upstream query

- **URL / endpoint**: `http://localhost:5025/api/lineage/upstream`
- **Capture**: upstream response for an important target column, such as revenue in a gold mart.
- **Demonstrates**:
  - reviewers can trace a target column back to its source columns;
  - lineage metadata is persisted and served by the API;
  - transformation descriptions are available for audit and explanation.

## Lineage impact analysis

- **URL / endpoint**: `http://localhost:5025/api/lineage/impact`
- **Capture**: impact response for a source column such as a customer or order field.
- **Demonstrates**:
  - the system can answer: if this source column changes, what downstream columns or marts break;
  - lineage is operational metadata, not a hand-drawn static diagram;
  - multi-hop lineage is supported.

## DAG run history with per-task timings

- **URL / endpoint**: the run-history view or API endpoint exposed by the application for DAG run history.
- **Capture**: a successful 26-task seed run with task names, timings, and row counts.
- **Demonstrates**:
  - deterministic topological execution;
  - per-task observability;
  - rows-in/rows-out accounting;
  - run-level success state;
  - the seed run's approximate duration and throughput.
- **Expected seed-run values**:
  - 26 tasks;
  - `success=true`;
  - about 1.3 seconds;
  - `totalRowsOut` about 9,577.

## Circuit-breaker blocked run

- **URL / endpoint**: the run-history or DQ-report view exposed by the application after `scripts\demo.ps1` breaks a quality rule.
- **Capture**: fail-severity DQ result with downstream gold tasks marked `Blocked`.
- **Demonstrates**:
  - a critical data-quality failure raises `CircuitBreakerException`;
  - promotion to gold stops;
  - bad data is not silently served;
  - run history distinguishes failed, blocked, and successful work.

## SQL API successful read

- **URL / endpoint**: the SQL query API route exposed by the application on `http://localhost:5025`
- **Capture**: request and response for a single-statement `SELECT` over a gold mart.
- **Demonstrates**:
  - serving uses SQLite over gold tables;
  - SQL access is authenticated;
  - the connection is read-only;
  - callers receive bounded results.
- **Suggested query**:

```sql
SELECT channel, ROUND(SUM(revenue_usd), 2) AS revenue_usd
FROM agg_daily_revenue
WHERE date >= '2026-01-01' AND date < '2026-02-01'
GROUP BY channel
ORDER BY channel;
```

## SQL API rejection: write or injection attempt

- **URL / endpoint**: the SQL query API route exposed by the application on `http://localhost:5025`
- **Capture**: a write, DDL, batched statement, comment-based injection, or forbidden keyword request returning HTTP 400.
- **Demonstrates**:
  - the SQL API is not a raw unrestricted database tunnel;
  - only single-statement `SELECT`/`WITH` requests are allowed;
  - forbidden keywords such as `INSERT`, `UPDATE`, `DELETE`, `DROP`, `ALTER`, `CREATE`, `PRAGMA`, and `ATTACH` are rejected;
  - statement batching and comments are rejected;
  - the serving boundary is gold-only and PII is not loaded into SQLite.
- **Suggested request body**:

```json
{
  "sql": "DROP TABLE agg_daily_revenue",
  "limit": 100
}
```

## Metrics-layer query

- **URL / endpoint**: the metrics query endpoint exposed by the application on `http://localhost:5025`.
- **Capture**: a `revenue_usd` query grouped by `channel` for January 2026.
- **Demonstrates**:
  - named metrics have definitions;
  - dimensions are allow-listed;
  - `TimeGrain` is a closed enum;
  - SQL generation is injection-safe.

## Table-format time travel evidence

- **URL / endpoint**: local output from the demo or an application view that shows snapshot versions.
- **Capture**: snapshot or manifest evidence showing that readers can query as of snapshot N or timestamp T.
- **Demonstrates**:
  - the custom table format supports atomic commits, snapshot isolation, time travel, schema versions, MERGE-style upsert, and delete-by-predicate;
  - data files are JSONL by design in this implementation.

