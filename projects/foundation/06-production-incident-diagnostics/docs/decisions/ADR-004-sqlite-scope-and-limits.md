# ADR-004: Use SQLite for the default persistence and index demonstrations

## Context
The build host has no database server. The sample app must demonstrate EF Core persistence, a realistic query pattern, and query-plan evidence without external infrastructure.

## Options
1. Use an in-memory object store only.
2. Require Postgres or SQL Server.
3. Use SQLite for the default adapter and document its limits.

## Decision
Use EF Core 10 with SQLite for Northstar and `Microsoft.Data.Sqlite` in the index scenario. The missing-index scenario executes `EXPLAIN QUERY PLAN` against the same connection and predicate used for timing.

## Consequences
The repository has real SQL, constraints, indexes, and an actual `SCAN`/`SEARCH ... USING INDEX` plan. SQLite’s `DateTimeOffset` limitation is handled through UTC Unix-millisecond value converters.

## Risks
SQLite has no server connection pool, parallel query engine, production cardinality estimator, or locking semantics equivalent to a network RDBMS. `INC-003` therefore uses an explicit bounded lease pool around real SQLite connections and reports that limitation.

## Alternatives
A future production-adapter profile can add Npgsql/Postgres and record native connection-pool errors, but it would remain optional so CI stays self-contained.
