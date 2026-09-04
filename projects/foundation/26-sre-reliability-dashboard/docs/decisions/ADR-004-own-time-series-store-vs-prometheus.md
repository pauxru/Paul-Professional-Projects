# ADR-004: Use an EF Core SQLite Metrics Store Rather Than Prometheus

## Context
The project must run with no external infrastructure. Prometheus and Grafana are unavailable on the build host, yet the implementation must demonstrate real metric retention, aggregation, and SLO querying.

## Options
1. Require Prometheus/Grafana.
2. Fake telemetry in dashboard-only JSON.
3. Persist aggregate metric samples in SQLite and implement the SLO query semantics locally.

## Decision
Choose option 3. SQLite rows hold scoped aggregates, percentile summaries, and histogram buckets. A retention engine rolls aged raw samples into hourly aggregates and expires older data.

## Consequences
The entire project builds and tests locally. The SLI evaluator receives explicit metric samples, making mathematical fixtures small and inspectable.

## Risks
This is not a high-cardinality time-series engine, and it does not implement PromQL, remote write, scrape discovery, or Grafana visualization.

## Alternatives
In production, map `MetricSample` fields to Prometheus counters/histograms and evaluate recording rules or a dedicated SLO backend. That mapping is future work, not a claim that this local store replaces Prometheus operationally.
