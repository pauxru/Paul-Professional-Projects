# Test Results

Executed on 2026-09-03 on Windows with .NET SDK 10.0.400. SQLite is the default runtime store; integration tests hold an in-memory SQLite connection open for the fixture lifetime. No Docker or external provider was used.

## `dotnet build -c Release`

```text
Determining projects to restore...
  All projects are up-to-date for restore.
  LoanOrigination.Domain -> C:\Users\rukwaropaul\Downloads\DEV\Projects\14-loan-origination-platform\src\LoanOrigination.Domain\bin\Release\net10.0\LoanOrigination.Domain.dll
  LoanOrigination.Application -> C:\Users\rukwaropaul\Downloads\DEV\Projects\14-loan-origination-platform\src\LoanOrigination.Application\bin\Release\net10.0\LoanOrigination.Application.dll
  LoanOrigination.Infrastructure -> C:\Users\rukwaropaul\Downloads\DEV\Projects\14-loan-origination-platform\src\LoanOrigination.Infrastructure\bin\Release\net10.0\LoanOrigination.Infrastructure.dll
  LoanOrigination.UnitTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\14-loan-origination-platform\tests\LoanOrigination.UnitTests\bin\Release\net10.0\LoanOrigination.UnitTests.dll
  LoanOrigination.Api -> C:\Users\rukwaropaul\Downloads\DEV\Projects\14-loan-origination-platform\src\LoanOrigination.Api\bin\Release\net10.0\LoanOrigination.Api.dll
  LoanOrigination.IntegrationTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\14-loan-origination-platform\tests\LoanOrigination.IntegrationTests\bin\Release\net10.0\LoanOrigination.IntegrationTests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:09.46
```

## `dotnet test -c Release`

```text
Determining projects to restore...
  All projects are up-to-date for restore.
  LoanOrigination.Domain -> C:\Users\rukwaropaul\Downloads\DEV\Projects\14-loan-origination-platform\src\LoanOrigination.Domain\bin\Release\net10.0\LoanOrigination.Domain.dll
  LoanOrigination.Application -> C:\Users\rukwaropaul\Downloads\DEV\Projects\14-loan-origination-platform\src\LoanOrigination.Application\bin\Release\net10.0\LoanOrigination.Application.dll
  LoanOrigination.Infrastructure -> C:\Users\rukwaropaul\Downloads\DEV\Projects\14-loan-origination-platform\src\LoanOrigination.Infrastructure\bin\Release\net10.0\LoanOrigination.Infrastructure.dll
  LoanOrigination.UnitTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\14-loan-origination-platform\tests\LoanOrigination.UnitTests\bin\Release\net10.0\LoanOrigination.UnitTests.dll
Test run for C:\Users\rukwaropaul\Downloads\DEV\Projects\14-loan-origination-platform\tests\LoanOrigination.UnitTests\bin\Release\net10.0\LoanOrigination.UnitTests.dll (.NETCoreApp,Version=v10.0)
  LoanOrigination.Api -> C:\Users\rukwaropaul\Downloads\DEV\Projects\14-loan-origination-platform\src\LoanOrigination.Api\bin\Release\net10.0\LoanOrigination.Api.dll
A total of 1 test files matched the specified pattern.
  LoanOrigination.IntegrationTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\14-loan-origination-platform\tests\LoanOrigination.IntegrationTests\bin\Release\net10.0\LoanOrigination.IntegrationTests.dll
Test run for C:\Users\rukwaropaul\Downloads\DEV\Projects\14-loan-origination-platform\tests\LoanOrigination.IntegrationTests\bin\Release\net10.0\LoanOrigination.IntegrationTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    68, Skipped:     0, Total:    68, Duration: 312 ms - LoanOrigination.UnitTests.dll (net10.0)

Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5, Duration: 1 s - LoanOrigination.IntegrationTests.dll (net10.0)
```

Aggregate: **73 passed, 0 failed, 0 skipped**.
