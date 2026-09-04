# Verification Results

**Run date:** 2026-09-03
**Host:** Windows, .NET SDK 10.0.400, `net10.0`
**Infrastructure:** none required; SQLite is local/in-memory for tests. Local absolute paths are redacted below.

## `dotnet build -c Release`

Real final console summary:
```text
Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:58.53
```

All nine projects were built: `Lab.Domain`, `Lab.Application`, `Lab.Infrastructure`, `Lab.Diagnostics`, `Lab.Scenarios`, `Lab.SampleApp`, `Lab.Harness`, `Lab.UnitTests`, and `Lab.IntegrationTests`.

## `dotnet test -c Release`

Real final console summary:
```text
Test run for [redacted]\Lab.UnitTests.dll (.NETCoreApp,Version=v10.0)
Test run for [redacted]\Lab.IntegrationTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    25, Skipped:     0, Total:    25, Duration: 7 s - Lab.UnitTests.dll (net10.0)

Passed!  - Failed:     0, Passed:     9, Skipped:     0, Total:     9, Duration: 3 s - Lab.IntegrationTests.dll (net10.0)
```

**Aggregate:** 34 passed, 0 failed, 0 skipped. Incident tests use `[Trait("Category", "Incident")]`, cancellation tokens, small bounded workloads, and no external services.

## Evidence generation

`.\scripts\run-all-scenarios.ps1 -Requests 20 -TimeoutSeconds 30` was also run successfully. It regenerated both JSON and Markdown evidence for `INC-001` through `INC-010`. A separate real Development API run produced [`evidence/sample-api-otel-console.txt`](evidence/sample-api-otel-console.txt), containing ASP.NET Core, EF Core, custom activity, and metric console-exporter output.
