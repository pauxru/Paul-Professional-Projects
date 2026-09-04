# INC-002 — Missing database index (Broken)

- Started (UTC): 2026-09-02T22:33:39.6330166+00:00
- Requested operations: 20000
- Elapsed: 246.68 ms
- Completed within budget: True

## Metrics

- **seededRows:** 20000
- **rowsFoundAcrossFiveQueries:** 5
- **queryP50Milliseconds:** 1.60
- **queryP95Milliseconds:** 2.41
- **sqliteQueryPlan:** SCAN CargoEvents
- **indexCreated:** False

## Evidence

- SQLite EXPLAIN QUERY PLAN was captured from the same connection and SQL predicate used for timing.
- Plan: SCAN CargoEvents

## Limitations

- SQLite query plans demonstrate scan versus index selection; production cardinality estimates and I/O behaviour differ by database engine.
