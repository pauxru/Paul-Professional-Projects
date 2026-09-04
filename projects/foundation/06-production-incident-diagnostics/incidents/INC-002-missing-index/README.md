# INC-002 — Missing database index

## Symptoms
A selective order/event lookup slows as table size grows while CPU can remain moderate. Database plans show a table scan rather than an indexed search.

## Business impact
Repeated scans waste I/O and crowd out critical reads. A dispatch status lookup that is cheap in a small test dataset can become a source of user-visible tail latency as history grows.

## Reproduction
```powershell
dotnet run --project src\Lab.Harness -c Release -- --scenario INC-002 --mode broken --requests 20
dotnet run --project src\Lab.Harness -c Release -- --scenario INC-002 --mode fixed --requests 20
```

## Telemetry & evidence
The harness seeded **20,000** in-memory SQLite `CargoEvents` rows and ran the same predicate five times. Evidence is at [`evidence\inc-002-broken.json`](evidence/inc-002-broken.json) and [`evidence\inc-002-fixed.json`](evidence/inc-002-fixed.json).

| Run | p50 | p95 | Actual SQLite plan |
|---|---:|---:|---|
| Broken | 1.603 ms | 2.409 ms | `SCAN CargoEvents` |
| Fixed | 0.029 ms | 0.121 ms | `SEARCH CargoEvents USING INDEX IX_CargoEvents_LookupCode (LookupCode=?)` |

Captured plan excerpt:
```text
Broken: SCAN CargoEvents
Fixed:  SEARCH CargoEvents USING INDEX IX_CargoEvents_LookupCode (LookupCode=?)
```

## Hypotheses considered and eliminated
- **N+1 query fan-out:** exactly five predicate executions occur in both modes.
- **Connection acquisition:** both plans run through the same already-open SQLite connection.
- **Payload deserialization:** the query shape and row result are unchanged; only index creation changes.

## Root cause
`LookupCode` is a selective lookup field with no index in broken mode, requiring SQLite to scan `CargoEvents`.

## The fix
Create an explicit lookup index through the persistence migration/configuration path:
```diff
- CREATE TABLE CargoEvents (... LookupCode TEXT NOT NULL ...);
+ CREATE TABLE CargoEvents (... LookupCode TEXT NOT NULL ...);
+ CREATE INDEX IX_CargoEvents_LookupCode ON CargoEvents (LookupCode);
```

## Verification
The same captured predicate moved from `SCAN` to `SEARCH ... USING INDEX`; p95 changed from **2.409 ms to 0.121 ms** in this synthetic run. The test asserts plan category and a relative p95 improvement, not a production timing claim.

## Prevention
Require a migration/configuration review for every lookup predicate, keep representative plan evidence for hot paths, and alert on slow-query distribution by normalized statement. Indexes should be validated for write overhead and selectivity, not added indiscriminately.

## Related failure modes
Non-sargable predicates, implicit conversions, stale statistics in server databases, missing composite indexes, and unbounded result sets can all look like a missing single-column index.
