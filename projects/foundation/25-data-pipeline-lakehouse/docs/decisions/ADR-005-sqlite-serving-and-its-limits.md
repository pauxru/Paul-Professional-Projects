# ADR-005: SQLite as the serving/query engine, and its limits

- Status: Accepted
- Date: 2026-09
- Deciders: Solo engineer (self-directed case study)

## Context

The gold star schema must be queryable through a SQL API, a metrics/semantic layer, and a dashboard —
with **zero external infrastructure** (no Postgres/Synapse/warehouse). We also need a hard security
boundary: ad-hoc SQL from clients must not be able to write, run DDL, inject, or read PII.

## Options considered

- **A. Query the lake files directly (JSONL scan) in-process.** No dependency, but every query would be
  a full row-oriented scan with hand-written filtering/aggregation — slow and a lot of engine code to
  reimplement (joins, group-by, order-by).
- **B. DuckDB (embedded OLAP).** The natural analytical fit and resolves on the feed, but adds a native
  engine and reframes the serving story around DuckDB rather than a clean, ubiquitous default.
- **C. SQLite via raw `Microsoft.Data.Sqlite`.** Ubiquitous, embedded, zero-config, real SQL (joins,
  CTEs, aggregates), a genuine **read-only** connection mode, per-command timeouts, and it ships with
  the SDK story. Not a columnar OLAP engine, but more than adequate for the demo marts.

## Decision

Adopt **C**. Only the **gold serving tables** are projected into a SQLite database
(`SqliteQueryEngine.Rebuild`). Queries run on a dedicated **read-only** connection with a row cap and a
statement timeout. Every statement first passes `SqlGuard` (single `SELECT`/`WITH` only; no comments;
no statement batching; a forbidden-keyword allow-list rejecting `INSERT/UPDATE/DELETE/DROP/ALTER/
CREATE/PRAGMA/ATTACH/...`). The metrics layer never lets clients write SQL: it resolves named metrics +
allow-listed dimensions + a closed `TimeGrain` enum into injection-safe SQL.

Crucially, **bronze and silver (where PII lives) are never loaded into SQLite** — the query surface is
gold-only, giving a clean data boundary by construction.

## Consequences

- Positive: real SQL serving with no infrastructure; strong, layered guards that are tested
  (`SqlGuardTests`, `ServingEngineTests`, and integration tests proving writes/DDL/injection return
  400). PII cannot leak through the query API because it is not present in the serving store.
- Positive: EF Core is intentionally **not** used — the engine is a synchronous batch component and raw
  `Microsoft.Data.Sqlite` keeps the guarantees (read-only mode, timeouts) explicit and obvious.
- Negative / honest limits: SQLite is row-oriented, single-writer, and not a distributed MPP warehouse;
  large analytical scans are slower than a columnar engine; `Decimal` is stored as TEXT to preserve
  precision (queries must be aware); there is no partitioning/clustering. For the demo mart sizes this
  is fine; production analytics at scale would map to Synapse Serverless / a warehouse (see
  `docs/azure-mapping.md`).

## Risks

- A future contributor could load silver/bronze into the serving DB and break the PII boundary.
  Mitigation: `Rebuild` iterates a fixed `GoldServingTables` allow-list, and an integration test asserts
  no `bronze_`/`silver_` tables are present in the serving store.
- Guard bypass via a novel injection vector. Mitigation: defence in depth — the guard **and** a truly
  read-only connection **and** the metrics layer that never accepts raw SQL; parameterised loading.

## Alternatives not chosen

Direct file scans (A) — reimplements a SQL engine badly. DuckDB (B) — adds a native dependency and
shifts the narrative; kept as a documented future option since the query path is behind
`ISqlQueryEngine`.
