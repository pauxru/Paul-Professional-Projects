# Test Results

Run on 2026-09-03 using Windows and .NET SDK 10.0.400. No Docker, database server, or external service was used. Integration tests use a kept-open SQLite in-memory connection.

## Build command

```text
> dotnet build -c Release
  Determining projects to restore...
  All projects are up-to-date for restore.
  FeatureFlags.Domain -> C:\Users\rukwaropaul\Downloads\DEV\Projects\18-feature-flag-service\src\FeatureFlags.Domain\bin\Release\net10.0\FeatureFlags.Domain.dll
  FeatureFlags.Application -> C:\Users\rukwaropaul\Downloads\DEV\Projects\18-feature-flag-service\src\FeatureFlags.Application\bin\Release\net10.0\FeatureFlags.Application.dll
  FeatureFlags.Infrastructure -> C:\Users\rukwaropaul\Downloads\DEV\Projects\18-feature-flag-service\src\FeatureFlags.Infrastructure\bin\Release\net10.0\FeatureFlags.Infrastructure.dll
  FeatureFlags.Sdk -> C:\Users\rukwaropaul\Downloads\DEV\Projects\18-feature-flag-service\src\FeatureFlags.Sdk\bin\Release\net10.0\FeatureFlags.Sdk.dll
  FeatureFlags.Api -> C:\Users\rukwaropaul\Downloads\DEV\Projects\18-feature-flag-service\src\FeatureFlags.Api\bin\Release\net10.0\FeatureFlags.Api.dll
  DemoApp -> C:\Users\rukwaropaul\Downloads\DEV\Projects\18-feature-flag-service\samples\DemoApp\bin\Release\net10.0\DemoApp.dll
  FeatureFlags.UnitTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\18-feature-flag-service\tests\FeatureFlags.UnitTests\bin\Release\net10.0\FeatureFlags.UnitTests.dll
  FeatureFlags.IntegrationTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\18-feature-flag-service\tests\FeatureFlags.IntegrationTests\bin\Release\net10.0\FeatureFlags.IntegrationTests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:05.41
```

## Test command

```text
> dotnet test -c Release
  Determining projects to restore...
  All projects are up-to-date for restore.
  FeatureFlags.Domain -> C:\Users\rukwaropaul\Downloads\DEV\Projects\18-feature-flag-service\src\FeatureFlags.Domain\bin\Release\net10.0\FeatureFlags.Domain.dll
  FeatureFlags.Application -> C:\Users\rukwaropaul\Downloads\DEV\Projects\18-feature-flag-service\src\FeatureFlags.Application\bin\Release\net10.0\FeatureFlags.Application.dll
  FeatureFlags.Sdk -> C:\Users\rukwaropaul\Downloads\DEV\Projects\18-feature-flag-service\src\FeatureFlags.Sdk\bin\Release\net10.0\FeatureFlags.Sdk.dll
  FeatureFlags.Infrastructure -> C:\Users\rukwaropaul\Downloads\DEV\Projects\18-feature-flag-service\src\FeatureFlags.Infrastructure\bin\Release\net10.0\FeatureFlags.Infrastructure.dll
  FeatureFlags.UnitTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\18-feature-flag-service\tests\FeatureFlags.UnitTests\bin\Release\net10.0\FeatureFlags.UnitTests.dll
Test run for C:\Users\rukwaropaul\Downloads\DEV\Projects\18-feature-flag-service\tests\FeatureFlags.UnitTests\bin\Release\net10.0\FeatureFlags.UnitTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.
  FeatureFlags.Api -> C:\Users\rukwaropaul\Downloads\DEV\Projects\18-feature-flag-service\src\FeatureFlags.Api\bin\Release\net10.0\FeatureFlags.Api.dll
  FeatureFlags.IntegrationTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\18-feature-flag-service\tests\FeatureFlags.IntegrationTests\bin\Release\net10.0\FeatureFlags.IntegrationTests.dll
Test run for C:\Users\rukwaropaul\Downloads\DEV\Projects\18-feature-flag-service\tests\FeatureFlags.IntegrationTests\bin\Release\net10.0\FeatureFlags.IntegrationTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    57, Skipped:     0, Total:    57, Duration: 587 ms - FeatureFlags.UnitTests.dll (net10.0)

Passed!  - Failed:     0, Passed:    15, Skipped:     0, Total:    15, Duration: 4 s - FeatureFlags.IntegrationTests.dll (net10.0)
```

**Aggregate: 72 passed, 0 failed, 0 skipped.**
