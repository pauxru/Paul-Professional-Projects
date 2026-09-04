# Test Results

Run date: 2026-09-03  
Host: Windows, .NET SDK 10.0.400  
External infrastructure: none (SQLite, embedded loopback MQTT broker, and simulators only)

## `dotnet build -c Release`

Actual console summary:

```text
Determining projects to restore...
  All projects are up-to-date for restore.
  Iiot.Protocol -> ...\src\Iiot.Protocol\bin\Release\net10.0\Iiot.Protocol.dll
  Iiot.Domain -> ...\src\Iiot.Domain\bin\Release\net10.0\Iiot.Domain.dll
  Iiot.Application -> ...\src\Iiot.Application\bin\Release\net10.0\Iiot.Application.dll
  Iiot.Device -> ...\src\Iiot.Device\bin\Release\net10.0\Iiot.Device.dll
  Iiot.EdgeGateway -> ...\src\Iiot.EdgeGateway\bin\Release\net10.0\Iiot.EdgeGateway.dll
  Iiot.Broker -> ...\src\Iiot.Broker\bin\Release\net10.0\Iiot.Broker.dll
  Iiot.Infrastructure -> ...\src\Iiot.Infrastructure\bin\Release\net10.0\Iiot.Infrastructure.dll
  Iiot.Api -> ...\src\Iiot.Api\bin\Release\net10.0\Iiot.Api.dll
  Iiot.IntegrationTests -> ...\tests\Iiot.IntegrationTests\bin\Release\net10.0\Iiot.IntegrationTests.dll
  Iiot.UnitTests -> ...\tests\Iiot.UnitTests\bin\Release\net10.0\Iiot.UnitTests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:08.89
```

## `dotnet test -c Release`

Actual console summary:

```text
Determining projects to restore...
  All projects are up-to-date for restore.
Test run for ...\tests\Iiot.UnitTests\bin\Release\net10.0\Iiot.UnitTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.
Test run for ...\tests\Iiot.IntegrationTests\bin\Release\net10.0\Iiot.IntegrationTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    88, Skipped:     0, Total:    88, Duration: 1 s - Iiot.UnitTests.dll (net10.0)

Passed!  - Failed:     0, Passed:     9, Skipped:     0, Total:     9, Duration: 5 s - Iiot.IntegrationTests.dll (net10.0)
```

**Aggregate: 97 passed, 0 failed, 0 skipped.**

The suite includes real loopback TCP broker tests and SQLite-backed integration tests. Deterministic anomaly and edge replay measurements are recorded separately in `docs/anomaly-evaluation.md` and `docs/edge-buffering-test.md`.
