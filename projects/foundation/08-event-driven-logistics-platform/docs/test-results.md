# Test Results

## Environment

- Date: 2026-09-03
- OS: Windows
- SDK: .NET SDK 10.0.400
- Target: `net10.0`
- Database: SQLite; integration tests use a private named shared-cache in-memory database
  (`Mode=Memory;Cache=Shared`) so that EF opens one connection per `DbContext`, as in production
- External infrastructure: none

## Release build

Command:

```powershell
dotnet build -c Release
```

Real final output:

```text
Determining projects to restore...
All projects are up-to-date for restore.
SavannaLogistics.Domain -> src\SavannaLogistics.Domain\bin\Release\net10.0\SavannaLogistics.Domain.dll
SavannaLogistics.Application -> src\SavannaLogistics.Application\bin\Release\net10.0\SavannaLogistics.Application.dll
SavannaLogistics.Infrastructure -> src\SavannaLogistics.Infrastructure\bin\Release\net10.0\SavannaLogistics.Infrastructure.dll
SavannaLogistics.UnitTests -> tests\SavannaLogistics.UnitTests\bin\Release\net10.0\SavannaLogistics.UnitTests.dll
SavannaLogistics.Api -> src\SavannaLogistics.Api\bin\Release\net10.0\SavannaLogistics.Api.dll
SavannaLogistics.IntegrationTests -> tests\SavannaLogistics.IntegrationTests\bin\Release\net10.0\SavannaLogistics.IntegrationTests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:03.78
```

## Release tests

Command:

```powershell
dotnet test -c Release
```

Real final summaries:

```text
Passed!  - Failed:     0, Passed:    44, Skipped:     0, Total:    44, Duration: 5 s - SavannaLogistics.UnitTests.dll (net10.0)

Passed!  - Failed:     0, Passed:    14, Skipped:     0, Total:    14, Duration: 11 s - SavannaLogistics.IntegrationTests.dll (net10.0)
```

Combined result:

| Result | Count |
|---|---:|
| Passed | 58 |
| Failed | 0 |
| Skipped | 0 |
| Total | 58 |

## Measured benchmark tests

Command:

```powershell
dotnet test tests\SavannaLogistics.UnitTests -c Release `
  --filter "Category=Benchmark" --logger "console;verbosity=detailed"
```

Real output:

```text
ETA_BENCHMARK runs=100 predictions=1000 meanAbsoluteErrorSeconds=41.85 p90AbsoluteErrorSeconds=81.25 maxAbsoluteErrorSeconds=174.07
GEOFENCE_BENCHMARK geofences=2000 points=5000 bruteEvaluations=10000000 indexedEvaluations=10510 bruteMs=1919.25 indexedMs=12.75 matches=1527
```

10,000-ping command:

```powershell
dotnet test tests\SavannaLogistics.IntegrationTests -c Release `
  --filter "FullyQualifiedName~BatchPerformanceTests" `
  --logger "console;verbosity=detailed"
```

Real output:

```text
BATCH_BENCHMARK pings=10000 elapsedMs=3604.60 throughputPerSecond=2774.23
```

All performance inputs are deterministic synthetic data. The numbers are measurements from this host, not production claims.

## Test isolation: a flaky-test defect found and fixed

A repeated-run sweep (5x) exposed intermittent failures in
`Telemetry_BatchIngest_UpdatesPersistedProjection` and
`FleetRegistry_CreateDriverVehicleAndAssignment_HappyPath`. They passed individually and failed
only when the whole suite ran.

**Root cause.** The integration test factory injected a single `SqliteConnection` *instance* into
EF Core (`options.UseSqlite(_connection)`). `SqliteConnection` is not thread-safe, and this
project deliberately processes telemetry on a background hosted service. That background
processor and the HTTP request pipeline therefore used the same connection object concurrently,
producing `SQLite Error 5` (`SQLITE_BUSY`) inside `GetVehicleStateAsync`. The API surfaced it as
an RFC 7807 problem document, so the assertion failed with "requires an element of type 'Array',
but the target element has type 'Object'" - the misleading symptom of a real concurrency fault.

Notably, the test harness did not match production: production binds a connection *string*
(`options.UseSqlite(database.ConnectionString)`), so EF opens one connection per `DbContext`.
The bug existed only in the harness, which made the tests less faithful than the system they test.

**Fix.** The factory now uses a private, named, shared-cache in-memory database
(`Data Source=savanna-tests-<guid>;Mode=Memory;Cache=Shared;Default Timeout=30`) and holds one
keep-alive connection open for the fixture lifetime. EF now opens a connection per `DbContext`,
matching production, and `Default Timeout` makes contending readers wait instead of failing.

**Evidence after the fix** - five consecutive full runs:

```text
run 1 : passed=58 failed=0
run 2 : passed=58 failed=0
run 3 : passed=58 failed=0
run 4 : passed=58 failed=0
run 5 : passed=58 failed=0
```
