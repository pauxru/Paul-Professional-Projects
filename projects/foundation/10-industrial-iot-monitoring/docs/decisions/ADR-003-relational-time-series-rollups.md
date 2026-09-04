# ADR-003: Relational raw telemetry with materialized rollups

## Context
The project needs a no-infrastructure default while retaining efficient dashboard range queries and explicit retention boundaries.

## Options
1. Store every raw reading forever in SQLite.
2. Depend on a specialist time-series server.
3. Store raw readings plus 1-minute and 1-hour relational rollups.

## Decision
Choose option 3. SQLite raw telemetry is idempotent on `(device_id, sequence)`. Aggregation calculates min, max, average, count, and population standard deviation for typed metrics in minute and hour buckets; retention is applied by a clock-driven delete path.

## Consequences
The dashboard can query a bounded number of rollups while forensic detail remains available within raw retention. The rollup math is inspectable and tested without external infrastructure.

## Risks
On-demand recomputation is not appropriate for high ingest rates or multi-node writers. SQLite also has a single-writer constraint.

## Alternatives
TimescaleDB, InfluxDB, ClickHouse, or cloud telemetry stores are appropriate production adapters after real volume, retention, and query requirements are known.
