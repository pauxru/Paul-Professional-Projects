# Test Results

## Verification context

- Date: 2026-09-03
- Host: Windows
- SDK: .NET SDK 10.0.400
- Target framework: `net10.0`
- Database under integration test: SQLite in-memory with an open connection held by `ApiFactory`
- External infrastructure: none

## Release build

Command:

```powershell
dotnet build -c Release
```

Real output:

```text
  Determining projects to restore...
  All projects are up-to-date for restore.
  Northstar.Iga.Domain -> C:\Users\rukwaropaul\Downloads\DEV\Projects\24-identity-access-management\src\Northstar.Iga.Domain\bin\Release\net10.0\Northstar.Iga.Domain.dll
  Northstar.Iga.Application -> C:\Users\rukwaropaul\Downloads\DEV\Projects\24-identity-access-management\src\Northstar.Iga.Application\bin\Release\net10.0\Northstar.Iga.Application.dll
  Northstar.Iga.UnitTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\24-identity-access-management\tests\Northstar.Iga.UnitTests\bin\Release\net10.0\Northstar.Iga.UnitTests.dll
  Northstar.Iga.Infrastructure -> C:\Users\rukwaropaul\Downloads\DEV\Projects\24-identity-access-management\src\Northstar.Iga.Infrastructure\bin\Release\net10.0\Northstar.Iga.Infrastructure.dll
  Northstar.Iga.Api -> C:\Users\rukwaropaul\Downloads\DEV\Projects\24-identity-access-management\src\Northstar.Iga.Api\bin\Release\net10.0\Northstar.Iga.Api.dll
  Northstar.Iga.IntegrationTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\24-identity-access-management\tests\Northstar.Iga.IntegrationTests\bin\Release\net10.0\Northstar.Iga.IntegrationTests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:04.56
```

## Release tests

Command:

```powershell
dotnet test -c Release
```

Real output:

```text
  Determining projects to restore...
  All projects are up-to-date for restore.
  Northstar.Iga.Domain -> C:\Users\rukwaropaul\Downloads\DEV\Projects\24-identity-access-management\src\Northstar.Iga.Domain\bin\Release\net10.0\Northstar.Iga.Domain.dll
  Northstar.Iga.Application -> C:\Users\rukwaropaul\Downloads\DEV\Projects\24-identity-access-management\src\Northstar.Iga.Application\bin\Release\net10.0\Northstar.Iga.Application.dll
  Northstar.Iga.Infrastructure -> C:\Users\rukwaropaul\Downloads\DEV\Projects\24-identity-access-management\src\Northstar.Iga.Infrastructure\bin\Release\net10.0\Northstar.Iga.Infrastructure.dll
  Northstar.Iga.UnitTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\24-identity-access-management\tests\Northstar.Iga.UnitTests\bin\Release\net10.0\Northstar.Iga.UnitTests.dll
Test run for C:\Users\rukwaropaul\Downloads\DEV\Projects\24-identity-access-management\tests\Northstar.Iga.UnitTests\bin\Release\net10.0\Northstar.Iga.UnitTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.
  Northstar.Iga.Api -> C:\Users\rukwaropaul\Downloads\DEV\Projects\24-identity-access-management\src\Northstar.Iga.Api\bin\Release\net10.0\Northstar.Iga.Api.dll
  Northstar.Iga.IntegrationTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\24-identity-access-management\tests\Northstar.Iga.IntegrationTests\bin\Release\net10.0\Northstar.Iga.IntegrationTests.dll
Test run for C:\Users\rukwaropaul\Downloads\DEV\Projects\24-identity-access-management\tests\Northstar.Iga.IntegrationTests\bin\Release\net10.0\Northstar.Iga.IntegrationTests.dll (.NETCoreApp,Version=v10.0)

Passed!  - Failed:     0, Passed:    48, Skipped:     0, Total:    48, Duration: 132 ms - Northstar.Iga.UnitTests.dll (net10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    36, Skipped:     0, Total:    36, Duration: 5 s - Northstar.Iga.IntegrationTests.dll (net10.0)
```

## Summary

| Suite | Passed | Failed | Skipped | Total |
|---|---:|---:|---:|---:|
| Unit | 48 | 0 | 0 | 48 |
| Integration | 36 | 0 | 0 | 36 |
| **Combined** | **84** | **0** | **0** | **84** |

## Demo workflow verification

Command:

```powershell
.\scripts\demo.ps1
```

Real result summary:

```text
Joiner workflow: Completed
Access request: Fulfilled
Decision before=Deny, during=Allow, after=Deny; expired=1
Campaign generated 48 effective user-entitlement review items.
Auto-revoked=48; completion=100%
Demo complete.
```

Docker was unavailable on the host and was not run or verified.
