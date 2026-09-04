# Test Results

Executed from the repository root on 2026-09-03 using Windows, .NET SDK 10.0.400 / .NET runtime 10.0.11.

## Build

```text
> dotnet build -c Release

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:04.87
```

## Tests

```text
> dotnet test -c Release

Passed!  - Failed:     0, Passed:     7, Skipped:     0, Total:     7, Duration: 2 s - EnterpriseSearch.IntegrationTests.dll (net10.0)
Passed!  - Failed:     0, Passed:    50, Skipped:     0, Total:    50, Duration: 3 s - EnterpriseSearch.UnitTests.dll (net10.0)
```

**Aggregate: 57 passed, 0 failed, 0 skipped.**

The unit suite includes the seeded 6,000-document golden-set hybrid relevance regression and the 5,000-document indexing-throughput check. Integration tests use an open SQLite in-memory connection for the lifetime of `WebApplicationFactory<Program>`.
