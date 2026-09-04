# INC-002 — Missing database index (Fixed)

- Started (UTC): 2026-09-02T22:34:39.4461420+00:00
- Requested operations: 20000
- Elapsed: 365.99 ms
- Completed within budget: True

## Metrics

- **seededRows:** 20000
- **rowsFoundAcrossFiveQueries:** 5
- **queryP50Milliseconds:** 0.03
- **queryP95Milliseconds:** 0.12
- **sqliteQueryPlan:** SEARCH CargoEvents USING INDEX IX_CargoEvents_LookupCode (LookupCode=?)
- **indexCreated:** True

## Evidence

- SQLite EXPLAIN QUERY PLAN was captured from the same connection and SQL predicate used for timing.
- Plan: SEARCH CargoEvents USING INDEX IX_CargoEvents_LookupCode (LookupCode=?)

## Limitations

- SQLite query plans demonstrate scan versus index selection; production cardinality estimates and I/O behaviour differ by database engine.
