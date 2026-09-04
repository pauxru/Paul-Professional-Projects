# Test Results

## Verification context

- Date: 2026-09-03
- Host: Windows
- Runtime: .NET SDK 10.0.400, target `net10.0`
- Database under integration test: held-open SQLite in-memory connection
- External infrastructure: none

## Release build

Command:

```powershell
dotnet build -c Release
```

Real console output:

```text
  Determining projects to restore...
  All projects are up-to-date for restore.
  FieldOps.Domain -> C:\Users\rukwaropaul\Downloads\DEV\Projects\04-multitenant-b2b-saas\src\FieldOps.Domain\bin\Release\net10.0\FieldOps.Domain.dll
  FieldOps.Application -> C:\Users\rukwaropaul\Downloads\DEV\Projects\04-multitenant-b2b-saas\src\FieldOps.Application\bin\Release\net10.0\FieldOps.Application.dll
  FieldOps.Infrastructure -> C:\Users\rukwaropaul\Downloads\DEV\Projects\04-multitenant-b2b-saas\src\FieldOps.Infrastructure\bin\Release\net10.0\FieldOps.Infrastructure.dll
  FieldOps.Api -> C:\Users\rukwaropaul\Downloads\DEV\Projects\04-multitenant-b2b-saas\src\FieldOps.Api\bin\Release\net10.0\FieldOps.Api.dll
  FieldOps.UnitTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\04-multitenant-b2b-saas\tests\FieldOps.UnitTests\bin\Release\net10.0\FieldOps.UnitTests.dll
  FieldOps.IntegrationTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\04-multitenant-b2b-saas\tests\FieldOps.IntegrationTests\bin\Release\net10.0\FieldOps.IntegrationTests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:26.23
```

## Release tests

Command:

```powershell
dotnet test -c Release
```

Real console output:

```text
  Determining projects to restore...
  All projects are up-to-date for restore.
  FieldOps.Domain -> C:\Users\rukwaropaul\Downloads\DEV\Projects\04-multitenant-b2b-saas\src\FieldOps.Domain\bin\Release\net10.0\FieldOps.Domain.dll
  FieldOps.Application -> C:\Users\rukwaropaul\Downloads\DEV\Projects\04-multitenant-b2b-saas\src\FieldOps.Application\bin\Release\net10.0\FieldOps.Application.dll
  FieldOps.Infrastructure -> C:\Users\rukwaropaul\Downloads\DEV\Projects\04-multitenant-b2b-saas\src\FieldOps.Infrastructure\bin\Release\net10.0\FieldOps.Infrastructure.dll
  FieldOps.Api -> C:\Users\rukwaropaul\Downloads\DEV\Projects\04-multitenant-b2b-saas\src\FieldOps.Api\bin\Release\net10.0\FieldOps.Api.dll
  FieldOps.UnitTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\04-multitenant-b2b-saas\tests\FieldOps.UnitTests\bin\Release\net10.0\FieldOps.UnitTests.dll
  FieldOps.IntegrationTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\04-multitenant-b2b-saas\tests\FieldOps.IntegrationTests\bin\Release\net10.0\FieldOps.IntegrationTests.dll
Test run for C:\Users\rukwaropaul\Downloads\DEV\Projects\04-multitenant-b2b-saas\tests\FieldOps.UnitTests\bin\Release\net10.0\FieldOps.UnitTests.dll (.NETCoreApp,Version=v10.0)
Test run for C:\Users\rukwaropaul\Downloads\DEV\Projects\04-multitenant-b2b-saas\tests\FieldOps.IntegrationTests\bin\Release\net10.0\FieldOps.IntegrationTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    68, Skipped:     0, Total:    68, Duration: 3 s - FieldOps.UnitTests.dll (net10.0)

Passed!  - Failed:     0, Passed:    22, Skipped:     0, Total:    22, Duration: 8 s - FieldOps.IntegrationTests.dll (net10.0)
```

## Totals

| Suite | Passed | Failed | Skipped |
|---|---:|---:|---:|
| Unit | 68 | 0 | 0 |
| Integration | 22 | 0 | 0 |
| **Total** | **90** | **0** | **0** |

The integration suite includes real HTTP hosting and SQLite SQL semantics; no test is a placeholder assertion.
