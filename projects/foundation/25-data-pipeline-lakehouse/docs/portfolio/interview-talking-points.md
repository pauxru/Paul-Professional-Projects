# Interview Talking Points

Use these talking points to explain the project as an engineering system. The business is fictional Contoso Retail; the implementation, tests, and demo numbers are real.

## 1. Custom table format

### Problem

A lakehouse needs more than files in folders. Readers need stable snapshots, writers need atomic commits, schema needs versioning, and incremental processing needs predictable upsert/delete semantics.

### Approach

The project implements a small Delta-Lake-like table format from scratch:

- partitioned data files;
- manifest/transaction log;
- atomic commits;
- snapshot isolation for readers;
- time travel by snapshot number or timestamp;
- schema evolution with registered schema per version;
- MERGE-style upsert;
- delete-by-predicate;
- data files behind an `IDataFileFormat` abstraction.

The actual data-file format is JSONL. This was an explicit trade-off: `Parquet.Net` 6.1.0 resolved on the feed, but the untyped read path throws `Cannot create boxed ByRef-like values` on .NET 10. `DuckDB.NET` resolved but was not used.

### Evidence

Tests cover:

- atomic commit;
- snapshot isolation;
- time travel;
- MERGE upsert;
- delete-by-predicate;
- schema evolution reading old and new files.

Honest scope:

- no concurrent multi-writer protocol;
- no data-file compaction;
- no Z-ordering;
- single-process implementation;
- not a replacement for Delta Lake or Iceberg.

## 2. SCD2 and the effective-version join bug

### Problem

The classic SCD2 bug is joining a fact to the current dimension row instead of the dimension row that was valid when the event occurred. That produces historically incorrect analytics whenever customer or product attributes change.

### Approach

Customers and products are modeled as SCD Type 2 dimensions with:

- `valid_from`;
- `valid_to`;
- `is_current`;
- surrogate keys.

Gold fact loading joins to the dimension version effective at the event time. The current row is not assumed to be the correct row for historical events.

### Evidence

A test proves the behavior with two customer versions and two orders. Each order lands on the dimension version valid for its event time. The integration and unit test suite includes SCD2 correctness, out-of-order updates, and the effective-version join.

This is one of the strongest interview points because it shows dimensional-modeling correctness rather than only table creation.

## 3. Data-quality gates and circuit breaker

### Problem

Data-quality checks often become passive reports. If a critical expectation fails but downstream promotion continues, the platform still serves bad data.

### Approach

The project implements declarative expectations:

- `not_null`;
- `unique`;
- `accepted_range`;
- `accepted_values`;
- `referential_integrity`;
- `freshness`;
- `row_count_anomaly` against a rolling baseline;
- `distribution_drift`.

Each expectation has severity:

- `warn`: visible but non-blocking;
- `fail`: blocks promotion.

A fail-severity issue trips a circuit breaker. The DAG raises `CircuitBreakerException`, marks downstream gold tasks `Blocked`, and stops promotion.

### Evidence

The demo intentionally breaks a quality rule and shows the circuit breaker blocking gold. Tests cover every expectation type in both pass and fail cases, plus circuit-breaker behavior.

Seed-run DQ evidence:

- silver DQ gate: 14 passed, 1 warn-severity failure;
- warning: `referential_integrity:customer_id`, where 24 of 542 orders were orphan or late-arriving customers;
- gold DQ gate: 10 passed, 0 failed.

The warning is intentional: late/orphan customer references are visible at silver, then inferred as members at gold so referential integrity passes in the served model.

## 4. Incrementality and idempotency

### Problem

Incremental systems are difficult because retries, backfills, and replays can duplicate data or create inconsistent state.

### Approach

The pipeline uses:

- immutable bronze data;
- watermarks;
- checkpoint resume;
- deterministic silver rebuild behavior;
- deduplication by business key and sequence;
- idempotent processing for each window;
- date-range backfill;
- partial rerun of a task and its downstream closure.

Silver is deterministic from immutable bronze, which makes replay and recovery easier to reason about.

### Evidence

Tests cover:

- bronze immutability;
- idempotent re-ingest;
- checkpoint resume without duplication;
- late-arriving and out-of-order CDC;
- deduplication by business key;
- incremental run idempotency, where rerunning a window yields identical results;
- backfill of a range.

Demo evidence:

- seed run emits about 9,577 total output rows across the 26-task DAG;
- the full seed run completes successfully in about 1.3 seconds for the stated generator shape.

## 5. Orchestration

### Problem

A pipeline needs dependency ordering, retries, observability, controlled reruns, and protection against overlapping work on the same window.

### Approach

The orchestrator is a dependency DAG of 26 tasks. It executes in deterministic topological order using Kahn's algorithm. It supports:

- per-task retry budget;
- partial rerun of a task plus downstream closure;
- date-range backfill;
- run history;
- per-task timings;
- per-task row counts;
- concurrency control that rejects overlapping runs of the same window through `OverlappingRunException`;
- OpenTelemetry span per task.

### Evidence

Tests cover topological order, retry, partial rerun, and backfill. The demo seed run shows 26 tasks, `success=true`, approximately 1.3 seconds, and about 9,577 total rows out.

## 6. Column-level lineage and impact analysis

### Problem

Static lineage diagrams become stale. Table-level lineage is also too coarse for answering column-change impact questions.

### Approach

Transforms declare column-level lineage as part of the actual implementation. The system persists lineage and exposes it through API endpoints:

- Mermaid graph: `/api/lineage/mermaid`;
- upstream query: `/api/lineage/upstream`;
- impact query: `/api/lineage/impact`.

A lineage edge captures:

- target column;
- source column or columns;
- transformation description.

### Evidence

Tests cover multi-hop column-level lineage and impact analysis. The demo can show the Mermaid graph and then answer: if this source column changes, what downstream columns or marts are affected?

Strong interview phrasing:

> The graph is generated from transform metadata, not hand-drawn documentation.

## 7. Serving security and PII boundary

### Problem

An analytics SQL endpoint can become an unsafe database tunnel if it accepts arbitrary SQL. It can also leak sensitive data if raw or conformed PII is loaded into the serving database.

### Approach

The SQL API has layered guards:

- read-only SQLite connection;
- single-statement `SELECT`/`WITH` allow-list;
- forbidden keyword rejection for `INSERT`, `UPDATE`, `DELETE`, `DROP`, `ALTER`, `CREATE`, `PRAGMA`, `ATTACH`, and related unsafe operations;
- no SQL comments;
- no statement batching;
- row limits;
- timeouts;
- JWT bearer auth using HS256 for development;
- reader/operator roles.

The serving SQLite database contains only gold serving tables. PII remains in bronze and silver on the filesystem lake and is never loaded into SQLite.

The metrics layer further narrows access by exposing named metrics with definitions, allow-listed dimensions, and a closed `TimeGrain` enum that resolves to injection-safe SQL.

### Evidence

Tests cover SQL API rejection of writes, DDL, and injection attempts. Dashboard endpoints and metrics-layer SQL generation are also tested.

A good demo moment is sending `DROP TABLE agg_daily_revenue` to the SQL API and showing HTTP 400.

## 8. Testing strategy

### Problem

A portfolio data project is only credible if correctness is verified, especially for edge cases that commonly break production pipelines.

### Approach

The test suite is split between unit and integration coverage:

- unit tests for domain/application behavior and edge cases;
- integration tests for API, serving, dashboard endpoints, and end-to-end behaviors;
- Release test run as the correctness signal.

### Evidence

Real passing counts:

- 88 unit tests;
- 11 integration tests;
- 99 total tests;
- 0 failed.

Coverage includes:

- table-format semantics;
- bronze immutability and idempotent ingestion;
- checkpoint resume;
- late and out-of-order CDC;
- SCD2 effective joins;
- quarantine with reasons;
- all DQ expectation types;
- circuit breaker;
- incremental idempotency;
- backfill;
- DAG ordering/retry/partial rerun;
- lineage and impact analysis;
- metrics SQL generation;
- SQL API guards;
- dashboard endpoints;
- throughput over at least 100,000 synthetic rows within 120 seconds.

## 9. Azure mapping

### Problem

The project runs locally by design, but a reviewer may want to know how the concepts map to a cloud data platform.

### Approach

The mapping is conceptual and documented. No Azure infrastructure was provisioned.

Clean mapping:

- local filesystem lake maps to ADLS Gen2-style storage;
- bronze/silver/gold medallion layout maps to common lakehouse zones;
- custom table-format concepts map to Delta/Iceberg-style transaction logs and snapshots;
- DAG runner maps to a workflow orchestrator;
- data-quality gates map to governed promotion policies;
- lineage metadata maps to a catalog or governance plane;
- SQLite serving maps to a serving warehouse or query endpoint;
- OpenTelemetry spans and Serilog logs map to cloud observability.

### Evidence

The project is implemented locally with zero external infrastructure, which makes tests fast and deterministic. The architecture documentation explains the cloud mapping honestly without claiming provisioned resources.

## 10. Demo close

Use the real seed-run numbers as the close:

- 80 customers;
- 40 products;
- 600 orders;
- 400 sessions;
- 30 days;
- seed 42;
- 26 DAG tasks;
- about 1.3 seconds;
- about 9,577 total rows out;
- 99 tests passing.

Then state the honest limits:

- fictional business;
- local implementation;
- no provisioned cloud infrastructure;
- Docker not verified;
- JSONL files, not Parquet;
- custom table format is deliberately scoped and not a distributed Delta/Iceberg replacement.
