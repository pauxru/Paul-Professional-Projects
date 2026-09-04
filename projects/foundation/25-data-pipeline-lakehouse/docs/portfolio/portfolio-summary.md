# Data Pipeline & Analytics Lakehouse (Project 25)

## What it is

Data Pipeline & Analytics Lakehouse is a correct, incremental, testable medallion lakehouse built from first principles in .NET 10. It runs with zero external infrastructure: synthetic source systems emit a CDC-style change feed, the lake stores immutable and conformed data on the local filesystem, and SQLite is the serving engine behind a guarded SQL API, metrics layer, and dependency-free HTML/JS dashboard on port 5025.

The fictional business domain is Contoso Retail: orders, order lines, customers, products, web clickstream, inventory movements, and FX rates across KES, USD, GBP, and EUR. The business is fictional; the engineering implementation, tests, and demo outputs are real.

## Engineering story

The project is intentionally not a wrapper around a managed cloud service. It implements the core data-platform mechanics directly so the underlying concepts are visible and testable:

- append-only bronze ingestion from CDC source files;
- typed silver parsing with quarantine instead of silent row loss;
- SCD Type 2 dimensions with event-time joins;
- incremental gold facts, dimensions, and aggregate marts;
- a small Delta-Lake-like table format with manifests, snapshots, schema versions, and MERGE-style operations;
- data-quality gates with warn/fail severity and a circuit breaker;
- column-level lineage declared from actual transforms;
- guarded serving through SQLite, semantic metrics, JWT bearer auth, and a dashboard.

The documentation maps these concepts cleanly to Azure, but no cloud infrastructure was provisioned for this project.

## Concrete capabilities

- **Synthetic OLTP and CDC source feed**: emits insert, update, and delete operations with sequence and commit timestamp metadata.
- **Bronze layer**: raw, append-only, immutable, partitioned by ingest date, with source file, source offset, ingest time, run id, CDC op, sequence, and commit timestamp on every row.
- **Silver layer**: typed parsing, deterministic deduplication by business key and sequence, SCD2 customer and product dimensions, referential-integrity conformance, currency normalization through FX rates, timezone normalization, and rejection/quarantine with reason codes.
- **Gold layer**: star schema with `fact_order_line`, `fact_clickstream_session`, SCD2 `dim_customer`, SCD2 `dim_product`, `dim_date`, `dim_currency`, `dim_channel`, and incremental marts for daily revenue, cohort retention, funnel analysis, and inventory position.
- **Correct SCD2 fact loading**: facts join the dimension version effective at the event time, not the latest dimension row.
- **Late-arriving dimension handling**: orphan or late customer references are treated as warn-level silver issues and inferred into gold so gold referential integrity passes.
- **Custom table format**: partitioned JSONL data files behind an `IDataFileFormat` abstraction, manifest/transaction log, atomic commits, snapshot isolation, time travel by snapshot or timestamp, schema evolution, MERGE-style upsert, and delete-by-predicate.
- **Orchestration**: 26-task dependency DAG, deterministic topological execution with Kahn's algorithm, retries, partial rerun of a task plus downstream closure, date-range backfill, run history, row counts, timings, and overlapping-window rejection through `OverlappingRunException`.
- **Data quality**: declarative expectations for `not_null`, `unique`, `accepted_range`, `accepted_values`, `referential_integrity`, `freshness`, `row_count_anomaly`, and `distribution_drift`, with warn/fail severity, quarantine, and a circuit breaker that blocks gold promotion on critical failure.
- **Lineage**: automatically captured column-level lineage, persisted and queryable, rendered as Mermaid, with impact analysis for source-column changes.
- **Serving security**: read-only SQLite connection, single-statement `SELECT`/`WITH` allow-list, forbidden keyword rejection, no SQL comments, no batching, row limits, timeouts, JWT bearer auth using HS256 for development, reader/operator roles, and a PII boundary where gold-only serving tables are loaded into SQLite.
- **Observability**: structured Serilog logs, run and task ids, rows in/out, duration, failure rate, freshness gauges, and OpenTelemetry spans per DAG task.

## Demonstrated implementation skills

- .NET 10 and ASP.NET Core minimal APIs.
- Clean architecture across `Lakehouse.Domain`, `Lakehouse.Application`, `Lakehouse.Infrastructure`, and `Lakehouse.Api`.
- Raw `Microsoft.Data.Sqlite` for synchronous/batch serving without EF Core.
- Incremental data processing, checkpointing, and deterministic reprocessing.
- SCD Type 2 modeling and effective-time dimensional joins.
- Data-quality engineering, quarantine design, and promotion gates.
- Lineage modeling and impact analysis.
- Secure query API design and semantic SQL generation.
- OpenTelemetry instrumentation and structured logging.
- xUnit and integration testing with `Microsoft.AspNetCore.Mvc.Testing`.

## Evidence

The Release test suite is real and passing:

- 88 unit tests;
- 11 integration tests;
- 99 total tests;
- 0 failed.

The suite covers table-format atomic commit, snapshot isolation, time travel, MERGE upsert and delete, schema evolution, bronze immutability, idempotent re-ingest, checkpoint resume, late and out-of-order CDC, SCD2 correctness, effective-version fact joins, deduplication, quarantine reasons, every data-quality expectation type, circuit-breaker behavior, incremental idempotency, range backfill, DAG order/retry/partial rerun, multi-hop column lineage, impact analysis, metrics-layer SQL generation, SQL API rejection of writes/DDL/injection, dashboard endpoints, and throughput over at least 100,000 synthetic rows within 120 seconds.

Real demo run, using 80 customers, 40 products, 600 orders, 400 web sessions, 30 days, and seed 42:

- full seed DAG: 26 tasks, `success=true`, about 1.3 seconds, `totalRowsOut` about 9,577;
- January 2026 revenue by channel, USD:
  - mobile: 81,742.09;
  - partner: 76,050.58;
  - store: 104,075.23;
  - web: 102,948.39;
- `agg_daily_revenue` examples:
  - 2026-01-01: 16 orders, 9,014.45 USD;
  - 2026-01-03: 20 orders, 19,421.97 USD;
- silver DQ gate: 14 passed, 1 warn-severity failure for `referential_integrity:customer_id`; 24 of 542 orders were orphan or late-arriving customers and were inferred at gold;
- gold DQ gate: 10 passed, 0 failed.

## Honest scope and limitations

- Contoso Retail is fictional demo data, not a real client deployment.
- The project runs locally with zero external infrastructure; no Azure resources or other cloud infrastructure were provisioned.
- Docker was not verified.
- Data files are JSONL, not Parquet. `Parquet.Net` 6.1.0 resolved on the feed, but its untyped read path throws `Cannot create boxed ByRef-like values` on .NET 10. `DuckDB.NET` resolved but was not used.
- The custom table format is deliberately small. It demonstrates manifests, atomic single-process commits, snapshot isolation, time travel, schema versions, MERGE-style upsert, and predicate deletes, but it is not Delta Lake or Iceberg. It does not implement a concurrent multi-writer protocol, data-file compaction, Z-ordering, or distributed execution.
- JWT HS256 authentication is development-only.
