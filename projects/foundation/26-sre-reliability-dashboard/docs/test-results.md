# Test Results

Executed on 2026-09-03 on Windows with .NET SDK 10.0.400. No external infrastructure was running or required by the test suites; integration tests use an open SQLite `Data Source=:memory:` connection.

## `dotnet build -c Release`

```text
Determining projects to restore...
  All projects are up-to-date for restore.
  Northstar.Reliability.Domain -> C:\Users\rukwaropaul\Downloads\DEV\Projects\26-sre-reliability-dashboard\src\Northstar.Reliability.Domain\bin\Release\net10.0\Northstar.Reliability.Domain.dll
  Northstar.Reliability.Application -> C:\Users\rukwaropaul\Downloads\DEV\Projects\26-sre-reliability-dashboard\src\Northstar.Reliability.Application\bin\Release\net10.0\Northstar.Reliability.Application.dll
  Northstar.Reliability.Infrastructure -> C:\Users\rukwaropaul\Downloads\DEV\Projects\26-sre-reliability-dashboard\src\Northstar.Reliability.Infrastructure\bin\Release\net10.0\Northstar.Reliability.Infrastructure.dll
  Northstar.Reliability.UnitTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\26-sre-reliability-dashboard\tests\Northstar.Reliability.UnitTests\bin\Release\net10.0\Northstar.Reliability.UnitTests.dll
  Northstar.Reliability.Api -> C:\Users\rukwaropaul\Downloads\DEV\Projects\26-sre-reliability-dashboard\src\Northstar.Reliability.Api\bin\Release\net10.0\Northstar.Reliability.Api.dll
  Northstar.Reliability.IntegrationTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\26-sre-reliability-dashboard\tests\Northstar.Reliability.IntegrationTests\bin\Release\net10.0\Northstar.Reliability.IntegrationTests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:14.72
```

## `dotnet test -c Release`

```text
Determining projects to restore...
  All projects are up-to-date for restore.
  Northstar.Reliability.Domain -> C:\Users\rukwaropaul\Downloads\DEV\Projects\26-sre-reliability-dashboard\src\Northstar.Reliability.Domain\bin\Release\net10.0\Northstar.Reliability.Domain.dll
  Northstar.Reliability.Application -> C:\Users\rukwaropaul\Downloads\DEV\Projects\26-sre-reliability-dashboard\src\Northstar.Reliability.Application\bin\Release\net10.0\Northstar.Reliability.Application.dll
  Northstar.Reliability.UnitTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\26-sre-reliability-dashboard\tests\Northstar.Reliability.UnitTests\bin\Release\net10.0\Northstar.Reliability.UnitTests.dll
Test run for C:\Users\rukwaropaul\Downloads\DEV\Projects\26-sre-reliability-dashboard\tests\Northstar.Reliability.UnitTests\bin\Release\net10.0\Northstar.Reliability.UnitTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.
 
Passed!  - Failed:     0, Passed:    45, Skipped:     0, Total:    45, Duration: 485 ms - Northstar.Reliability.UnitTests.dll (net10.0)

  Northstar.Reliability.Infrastructure -> C:\Users\rukwaropaul\Downloads\DEV\Projects\26-sre-reliability-dashboard\src\Northstar.Reliability.Infrastructure\bin\Release\net10.0\Northstar.Reliability.Infrastructure.dll
  Northstar.Reliability.Api -> C:\Users\rukwaropaul\Downloads\DEV\Projects\26-sre-reliability-dashboard\src\Northstar.Reliability.Api\bin\Release\net10.0\Northstar.Reliability.Api.dll
  Northstar.Reliability.IntegrationTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\26-sre-reliability-dashboard\tests\Northstar.Reliability.IntegrationTests\bin\Release\net10.0\Northstar.Reliability.IntegrationTests.dll
Test run for C:\Users\rukwaropaul\Downloads\DEV\Projects\26-sre-reliability-dashboard\tests\Northstar.Reliability.IntegrationTests\bin\Release\net10.0\Northstar.Reliability.IntegrationTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.
Passed!  - Failed:     0, Passed:     7, Skipped:     0, Total:     7, Duration: 2 s - Northstar.Reliability.IntegrationTests.dll (net10.0)
```

**Total: 52 passed, 0 failed, 0 skipped.**

An end-to-end Development verification also ran `scripts\demo.ps1`: it generated 62 synthetic partial-outage samples, fired the fast-page alert, changed the `checkout` gate from allow to `FreezeAllChanges`, declared/mitigated/resolved an incident, attached budget attribution, and published a postmortem.
