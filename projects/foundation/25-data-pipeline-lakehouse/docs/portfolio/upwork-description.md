# Upwork Portfolio Description

## Data Pipeline & Analytics Lakehouse in .NET 10

I built a local, end-to-end data pipeline and analytics lakehouse that demonstrates the core mechanics of a modern data platform without relying on managed cloud infrastructure. The project implements a medallion architecture from first principles in .NET 10: raw CDC ingestion, typed conformance, data-quality gates, SCD Type 2 dimensions, incremental gold marts, column-level lineage, and a secure SQL/metrics serving layer backed by SQLite.

The result is a working portfolio system that shows how I design reliable analytical pipelines, not just how I call a vendor SDK.

## Problem it solves

Analytical systems often fail in predictable ways:

- source changes are loaded without enough metadata to replay or audit them;
- bad rows disappear silently;
- late-arriving records break referential integrity;
- facts join the current dimension version instead of the version valid at the event time;
- quality checks produce reports but do not stop bad data from promoting;
- lineage is drawn manually and drifts from the code;
- SQL endpoints become unsafe unrestricted database access.

This project addresses those issues directly in a compact, testable lakehouse implementation.

## What was built

- A synthetic OLTP generator for a fictional retail business with orders, order lines, customers, products, clickstream, inventory movements, and FX rates.
- A CDC feed with insert, update, delete, sequence, and commit timestamp metadata.
- A bronze layer that is raw, append-only, immutable, and partitioned by ingest date.
- A silver layer with typed parsing, quarantine with rejection reasons, deduplication, SCD2 customers and products, referential-integrity conformance, currency normalization, and timezone normalization.
- A gold star schema with facts, SCD2 dimensions, date/currency/channel dimensions, and aggregate marts for daily revenue, cohort retention, funnel, and inventory position.
- A custom Delta-Lake-like table format using partitioned JSONL data files, manifests, atomic commits, snapshot isolation, time travel, schema evolution, MERGE-style upsert, and delete-by-predicate.
- A deterministic 26-task DAG runner with retries, partial rerun, date-range backfill, run history, row counts, timings, and overlapping-run protection.
- A declarative data-quality framework with warn/fail severity, quarantine, and a circuit breaker that blocks gold promotion on critical failures.
- Column-level lineage with upstream and impact-analysis queries and Mermaid rendering.
- A guarded SQL API over SQLite, a metrics/semantic layer, JWT bearer auth, reader/operator roles, and a dependency-free HTML/JS dashboard.

## Technology used

- .NET 10 (`net10.0`)
- ASP.NET Core minimal APIs
- Raw `Microsoft.Data.Sqlite`
- Serilog structured logging
- OpenTelemetry with console exporter
- JWT bearer authentication using HS256 for development
- xUnit 2.9.3
- `Microsoft.AspNetCore.Mvc.Testing`
- Clean architecture: Domain, Application, Infrastructure, Api, UnitTests, IntegrationTests

No EF Core is used because the serving engine is synchronous and batch-oriented.

## Evidence of correctness

The project has a real passing Release test suite:

- 88 unit tests;
- 11 integration tests;
- 99 total tests;
- 0 failed.

The tests cover table-format atomic commit, snapshot isolation, time travel, MERGE upsert and delete, schema evolution, bronze immutability, idempotent re-ingest, checkpoint resume, late and out-of-order CDC, SCD2 behavior, effective-version joins, deduplication, quarantine, all data-quality expectation types, circuit-breaker blocking, incremental idempotency, range backfill, DAG retry and partial rerun, column-level lineage, impact analysis, metrics SQL generation, SQL API rejection of unsafe statements, dashboard endpoints, and throughput over at least 100,000 synthetic rows within 120 seconds.

A real demo run using 80 customers, 40 products, 600 orders, 400 sessions, 30 days, and seed 42 produced:

- full 26-task DAG success in about 1.3 seconds;
- about 9,577 total output rows;
- January 2026 revenue by channel, USD: mobile 81,742.09, partner 76,050.58, store 104,075.23, web 102,948.39;
- silver DQ gate: 14 passed and 1 warn-severity failure for late/orphan customers;
- gold DQ gate: 10 passed and 0 failed.

## Value

This project demonstrates the engineering practices I would bring to a data-platform engagement:

- designing for replay, idempotency, and auditability;
- making data quality enforceable instead of cosmetic;
- handling SCD2 and late-arriving data correctly;
- protecting serving endpoints with layered SQL guards;
- capturing lineage from code rather than static diagrams;
- validating behavior with targeted unit and integration tests;
- documenting both the architecture and its limitations honestly.

## Fictional demo data honesty note

Contoso Retail is fictional demo data. The project is a portfolio implementation, not a production deployment for a real client. No cloud infrastructure was provisioned, Docker was not verified, and the table-format data files are JSONL rather than Parquet due to the documented .NET 10 `Parquet.Net` untyped read-path issue. The implementation is intentionally scoped: it demonstrates important lakehouse concepts, but it is not a replacement for Delta Lake or Iceberg in a concurrent distributed environment.
