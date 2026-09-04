# Test Results

**Real, pasted output** from running the suite on the build host. Nothing here is hand-edited or invented.

- Host: Windows (Microsoft Windows NT 10.0.26200.0)
- .NET SDK: **10.0.400**, target `net10.0`
- Configuration: **Release**
- Date: 2026-09-03 (+03:00)
- External infrastructure: **none** (SQLite embedded; no Docker/Postgres/Spark)

## `dotnet build -c Release Lakehouse.slnx`

```
Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:02.77
```

## `dotnet test -c Release Lakehouse.slnx --no-build`

```
Test run for ...\tests\Lakehouse.UnitTests\bin\Release\net10.0\Lakehouse.UnitTests.dll (.NETCoreApp,Version=v10.0)
Test run for ...\tests\Lakehouse.IntegrationTests\bin\Release\net10.0\Lakehouse.IntegrationTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    11, Skipped:     0, Total:    11, Duration: 1 s - Lakehouse.IntegrationTests.dll (net10.0)

Passed!  - Failed:     0, Passed:    88, Skipped:     0, Total:    88, Duration: 30 s - Lakehouse.UnitTests.dll (net10.0)
```

## Summary

| Suite | Passed | Failed | Skipped | Total |
|---|---|---|---|---|
| Lakehouse.UnitTests | 88 | 0 | 0 | 88 |
| Lakehouse.IntegrationTests | 11 | 0 | 0 | 11 |
| **Total** | **99** | **0** | **0** | **99** |

> The unit suite has 71 test methods; several `[Theory]` methods (notably in `SqlGuardTests`) expand into
> multiple `[InlineData]` cases, so the runner reports **88** executed unit cases. The minimum required by
> the spec is 30.

## Per-file test methods (`[Fact]` / `[Theory]`)

| File | Methods | Area |
|---|---:|---|
| TableFormatTests.cs | 7 | atomic commit, snapshot isolation, time travel, MERGE, delete, schema evolution |
| BronzeIngestionTests.cs | 6 | immutability, idempotent re-ingest, checkpoint resume |
| Scd2Tests.cs | 6 | SCD2 intervals incl. out-of-order updates |
| DimensionJoinTests.cs | 5 | effective-version resolver (the classic bug) |
| CdcSilverTests.cs | 4 | late-arriving / out-of-order CDC, dedup, quarantine |
| ExpectationTests.cs | 10 | every expectation type (pass + fail) |
| CircuitBreakerTests.cs | 3 | breaker blocks on blocking failure |
| GoldTests.cs | 4 | effective-version fact join, inferred member, FX→USD, agg sums |
| DagTests.cs | 7 | topological order, cycle detection, retry, partial re-run, backfill, overlap |
| PipelineTests.cs | 4 | idempotent re-run, backfill materialises gold, gate blocks promotion |
| LineageTests.cs | 3 | multi-hop lineage, impact analysis |
| MetricsTests.cs | 5 | metric→SQL generation, grains, unknown metric |
| SqlGuardTests.cs | 2 (Theory) | rejects writes/DDL/comments/batching/injection |
| ServingEngineTests.cs | 4 | read-only query, guard integration, gold-only load |
| ThroughputTests.cs | 1 | ≥ 100,000 synthetic rows within a bounded time |
| ApiTests.cs (integration) | 11 | auth 401/403, SQL 200/400, injection, dashboard, lineage, metrics |

All required test scenarios from the specification are covered and passing.
