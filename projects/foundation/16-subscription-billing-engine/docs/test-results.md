# Test Results

Verified on 2026-09-03 on Windows with .NET SDK 10.0.400. SQLite was local/in-memory; no external infrastructure was running.

## Release build

Command:

```powershell
dotnet build -c Release
```

Actual console output:

```text
  Determining projects to restore...
  Restored C:\Users\rukwaropaul\Downloads\DEV\Projects\16-subscription-billing-engine\tests\SubscriptionBilling.IntegrationTests\SubscriptionBilling.IntegrationTests.csproj (in 658 ms).
  5 of 6 projects are up-to-date for restore.
  SubscriptionBilling.Domain -> C:\Users\rukwaropaul\Downloads\DEV\Projects\16-subscription-billing-engine\src\SubscriptionBilling.Domain\bin\Release\net10.0\SubscriptionBilling.Domain.dll
  SubscriptionBilling.Application -> C:\Users\rukwaropaul\Downloads\DEV\Projects\16-subscription-billing-engine\src\SubscriptionBilling.Application\bin\Release\net10.0\SubscriptionBilling.Application.dll
  SubscriptionBilling.Infrastructure -> C:\Users\rukwaropaul\Downloads\DEV\Projects\16-subscription-billing-engine\src\SubscriptionBilling.Infrastructure\bin\Release\net10.0\SubscriptionBilling.Infrastructure.dll
  SubscriptionBilling.UnitTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\16-subscription-billing-engine\tests\SubscriptionBilling.UnitTests\bin\Release\net10.0\SubscriptionBilling.UnitTests.dll
  SubscriptionBilling.Api -> C:\Users\rukwaropaul\Downloads\DEV\Projects\16-subscription-billing-engine\src\SubscriptionBilling.Api\bin\Release\net10.0\SubscriptionBilling.Api.dll
  SubscriptionBilling.IntegrationTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\16-subscription-billing-engine\tests\SubscriptionBilling.IntegrationTests\bin\Release\net10.0\SubscriptionBilling.IntegrationTests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:03.55
```

## Release tests

Command:

```powershell
dotnet test -c Release
```

Actual console output:

```text
  Determining projects to restore...
  All projects are up-to-date for restore.
  SubscriptionBilling.Domain -> C:\Users\rukwaropaul\Downloads\DEV\Projects\16-subscription-billing-engine\src\SubscriptionBilling.Domain\bin\Release\net10.0\SubscriptionBilling.Domain.dll
  SubscriptionBilling.Application -> C:\Users\rukwaropaul\Downloads\DEV\Projects\16-subscription-billing-engine\src\SubscriptionBilling.Application\bin\Release\net10.0\SubscriptionBilling.Application.dll
  SubscriptionBilling.Infrastructure -> C:\Users\rukwaropaul\Downloads\DEV\Projects\16-subscription-billing-engine\src\SubscriptionBilling.Infrastructure\bin\Release\net10.0\SubscriptionBilling.Infrastructure.dll
  SubscriptionBilling.UnitTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\16-subscription-billing-engine\tests\SubscriptionBilling.UnitTests\bin\Release\net10.0\SubscriptionBilling.UnitTests.dll
Test run for C:\Users\rukwaropaul\Downloads\DEV\Projects\16-subscription-billing-engine\tests\SubscriptionBilling.UnitTests\bin\Release\net10.0\SubscriptionBilling.UnitTests.dll (.NETCoreApp,Version=v10.0)
  SubscriptionBilling.Api -> C:\Users\rukwaropaul\Downloads\DEV\Projects\16-subscription-billing-engine\src\SubscriptionBilling.Api\bin\Release\net10.0\SubscriptionBilling.Api.dll
A total of 1 test files matched the specified pattern.
  SubscriptionBilling.IntegrationTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\16-subscription-billing-engine\tests\SubscriptionBilling.IntegrationTests\bin\Release\net10.0\SubscriptionBilling.IntegrationTests.dll
Test run for C:\Users\rukwaropaul\Downloads\DEV\Projects\16-subscription-billing-engine\tests\SubscriptionBilling.IntegrationTests\bin\Release\net10.0\SubscriptionBilling.IntegrationTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    98, Skipped:     0, Total:    98, Duration: 2 s - SubscriptionBilling.UnitTests.dll (net10.0)

Passed!  - Failed:     0, Passed:    14, Skipped:     0, Total:    14, Duration: 4 s - SubscriptionBilling.IntegrationTests.dll (net10.0)
```

Combined result: **112 passed, 0 failed, 0 skipped**.

## Demo smoke run

`scripts\demo.ps1` was run against the local Development API on port 5016. It successfully created a fictional subscription, deduplicated usage, previewed/applied a plan change, generated one invoice across two runs, simulated insufficient funds, observed the dunning case, and recovered the invoice through a signed webhook. Docker was not available and was not tested.
