# Test results

*Captured 2026-09-03 02:00:29 +03:00* on Windows, .NET SDK 10.0.400, target `net10.0`.

Both build and test were run with:

```powershell
dotnet build -c Release --nologo
dotnet test  -c Release --nologo
```

Below is the actual pasted output from those runs.

## `dotnet build -c Release --nologo`

```
  Determining projects to restore...
  All projects are up-to-date for restore.
  LoadRunner.Core -> C:\Users\rukwaropaul\Downloads\DEV\Projects\27-api-performance-toolkit\src\LoadRunner.Core\bin\Release\net10.0\LoadRunner.Core.dll
  LoadRunner.Reporting -> C:\Users\rukwaropaul\Downloads\DEV\Projects\27-api-performance-toolkit\src\LoadRunner.Reporting\bin\Release\net10.0\LoadRunner.Reporting.dll
  SampleApi -> C:\Users\rukwaropaul\Downloads\DEV\Projects\27-api-performance-toolkit\src\SampleApi\bin\Release\net10.0\SampleApi.dll
  LoadRunner.UnitTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\27-api-performance-toolkit\tests\LoadRunner.UnitTests\bin\Release\net10.0\LoadRunner.UnitTests.dll
  LoadRunner.Cli -> C:\Users\rukwaropaul\Downloads\DEV\Projects\27-api-performance-toolkit\src\LoadRunner.Cli\bin\Release\net10.0\loadrun.dll
  LoadRunner.IntegrationTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\27-api-performance-toolkit\tests\LoadRunner.IntegrationTests\bin\Release\net10.0\LoadRunner.IntegrationTests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:02.75
```

## `dotnet test -c Release --nologo`

```
  Determining projects to restore...
  All projects are up-to-date for restore.
  LoadRunner.Core -> C:\Users\rukwaropaul\Downloads\DEV\Projects\27-api-performance-toolkit\src\LoadRunner.Core\bin\Release\net10.0\LoadRunner.Core.dll
  LoadRunner.Reporting -> C:\Users\rukwaropaul\Downloads\DEV\Projects\27-api-performance-toolkit\src\LoadRunner.Reporting\bin\Release\net10.0\LoadRunner.Reporting.dll
  LoadRunner.Cli -> C:\Users\rukwaropaul\Downloads\DEV\Projects\27-api-performance-toolkit\src\LoadRunner.Cli\bin\Release\net10.0\loadrun.dll
  SampleApi -> C:\Users\rukwaropaul\Downloads\DEV\Projects\27-api-performance-toolkit\src\SampleApi\bin\Release\net10.0\SampleApi.dll
  LoadRunner.UnitTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\27-api-performance-toolkit\tests\LoadRunner.UnitTests\bin\Release\net10.0\LoadRunner.UnitTests.dll
Test run for C:\Users\rukwaropaul\Downloads\DEV\Projects\27-api-performance-toolkit\tests\LoadRunner.UnitTests\bin\Release\net10.0\LoadRunner.UnitTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.
  LoadRunner.IntegrationTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\27-api-performance-toolkit\tests\LoadRunner.IntegrationTests\bin\Release\net10.0\LoadRunner.IntegrationTests.dll
Test run for C:\Users\rukwaropaul\Downloads\DEV\Projects\27-api-performance-toolkit\tests\LoadRunner.IntegrationTests\bin\Release\net10.0\LoadRunner.IntegrationTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    63, Skipped:     0, Total:    63, Duration: 3 s - LoadRunner.UnitTests.dll (net10.0)

Passed!  - Failed:     0, Passed:     9, Skipped:     0, Total:     9, Duration: 2 s - LoadRunner.IntegrationTests.dll (net10.0)
```

## Summary

- **Total tests:** 72 (63 unit + 9 integration)
- **Failed:** 0
- **Skipped:** 0
- **Runtime:** ~3 s unit, ~2 s integration
- **External infrastructure required:** none (SQLite in-memory for integration, no Docker, no network)

## Test file layout

```
tests/
├── LoadRunner.UnitTests/          (63 tests)
│   ├── Analysis/                   knee, drift, capacity, linear regression
│   ├── Assertions/                 threshold evaluator matrix
│   ├── LoadModels/                 closed model, open model rate, spike, stress dispatch
│   ├── Metrics/                    RequestSample, MetricsCollector aggregation
│   ├── Reporting/                  SVG structure, MD/HTML rendering, comparison
│   ├── Scenarios/                  JSON validation, templating, CSV feeder, extractor, think-time
│   └── Statistics/                 exact percentiles, histogram precision, Mann–Whitney U, bootstrap
└── LoadRunner.IntegrationTests/   (9 tests)
    ├── SampleApi/                  endpoint sanity, pathology mutation, seed
    └── Runner/                     ScenarioRunner end-to-end against WebApplicationFactory
```
