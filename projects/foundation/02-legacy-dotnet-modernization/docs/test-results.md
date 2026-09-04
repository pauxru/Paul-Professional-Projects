# Test Results

Run on 2026-09-03 from the repository root on Windows with .NET SDK 10.0.400. No Docker, database server, or other external infrastructure was used.

## `dotnet build -c Release`

```text
Determining projects to restore...
  All projects are up-to-date for restore.
  Northstar.Domain -> C:\Users\rukwaropaul\Downloads\DEV\Projects\02-legacy-dotnet-modernization\modern\src\Northstar.Domain\bin\Release\net10.0\Northstar.Domain.dll
  Northstar.Application -> C:\Users\rukwaropaul\Downloads\DEV\Projects\02-legacy-dotnet-modernization\modern\src\Northstar.Application\bin\Release\net10.0\Northstar.Application.dll
  Northstar.Infrastructure -> C:\Users\rukwaropaul\Downloads\DEV\Projects\02-legacy-dotnet-modernization\modern\src\Northstar.Infrastructure\bin\Release\net10.0\Northstar.Infrastructure.dll
  Northstar.UnitTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\02-legacy-dotnet-modernization\modern\tests\Northstar.UnitTests\bin\Release\net10.0\Northstar.UnitTests.dll
  Northstar.Api -> C:\Users\rukwaropaul\Downloads\DEV\Projects\02-legacy-dotnet-modernization\modern\src\Northstar.Api\bin\Release\net10.0\Northstar.Api.dll
  Northstar.IntegrationTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\02-legacy-dotnet-modernization\modern\tests\Northstar.IntegrationTests\bin\Release\net10.0\Northstar.IntegrationTests.dll
  Northstar.Legacy.Web -> C:\Users\rukwaropaul\Downloads\DEV\Projects\02-legacy-dotnet-modernization\legacy\Northstar.Legacy.Web\bin\Release\net10.0\Northstar.Legacy.Web.dll
  Northstar.Legacy.CharacterizationTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\02-legacy-dotnet-modernization\legacy\Northstar.Legacy.CharacterizationTests\bin\Release\net10.0\Northstar.Legacy.CharacterizationTests.dll
  Northstar.ModernizationTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\02-legacy-dotnet-modernization\tests\Northstar.ModernizationTests\bin\Release\net10.0\Northstar.ModernizationTests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:01:34.29
```

## `dotnet test -c Release`

```text
Determining projects to restore...
  All projects are up-to-date for restore.
  Northstar.Domain -> C:\Users\rukwaropaul\Downloads\DEV\Projects\02-legacy-dotnet-modernization\modern\src\Northstar.Domain\bin\Release\net10.0\Northstar.Domain.dll
  Northstar.Legacy.Web -> C:\Users\rukwaropaul\Downloads\DEV\Projects\02-legacy-dotnet-modernization\legacy\Northstar.Legacy.Web\bin\Release\net10.0\Northstar.Legacy.Web.dll
  Northstar.Application -> C:\Users\rukwaropaul\Downloads\DEV\Projects\02-legacy-dotnet-modernization\modern\src\Northstar.Application\bin\Release\net10.0\Northstar.Application.dll
  Northstar.Legacy.CharacterizationTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\02-legacy-dotnet-modernization\legacy\Northstar.Legacy.CharacterizationTests\bin\Release\net10.0\Northstar.Legacy.CharacterizationTests.dll
Test run for C:\Users\rukwaropaul\Downloads\DEV\Projects\02-legacy-dotnet-modernization\legacy\Northstar.Legacy.CharacterizationTests\bin\Release\net10.0\Northstar.Legacy.CharacterizationTests.dll (.NETCoreApp,Version=v10.0)
  Northstar.Infrastructure -> C:\Users\rukwaropaul\Downloads\DEV\Projects\02-legacy-dotnet-modernization\modern\src\Northstar.Infrastructure\bin\Release\net10.0\Northstar.Infrastructure.dll
  Northstar.UnitTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\02-legacy-dotnet-modernization\modern\tests\Northstar.UnitTests\bin\Release\net10.0\Northstar.UnitTests.dll
Test run for C:\Users\rukwaropaul\Downloads\DEV\Projects\02-legacy-dotnet-modernization\modern\tests\Northstar.UnitTests\bin\Release\net10.0\Northstar.UnitTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.
A total of 1 test files matched the specified pattern.
  Northstar.Api -> C:\Users\rukwaropaul\Downloads\DEV\Projects\02-legacy-dotnet-modernization\modern\src\Northstar.Api\bin\Release\net10.0\Northstar.Api.dll
  Northstar.ModernizationTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\02-legacy-dotnet-modernization\tests\Northstar.ModernizationTests\bin\Release\net10.0\Northstar.ModernizationTests.dll
Test run for C:\Users\rukwaropaul\Downloads\DEV\Projects\02-legacy-dotnet-modernization\tests\Northstar.ModernizationTests\bin\Release\net10.0\Northstar.ModernizationTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:     7, Skipped:     0, Total:     7, Duration: 341 ms - Northstar.Legacy.CharacterizationTests.dll (net10.0)

Passed!  - Failed:     0, Passed:    29, Skipped:     0, Total:    29, Duration: 122 ms - Northstar.UnitTests.dll (net10.0)
  Northstar.IntegrationTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\02-legacy-dotnet-modernization\modern\tests\Northstar.IntegrationTests\bin\Release\net10.0\Northstar.IntegrationTests.dll
Test run for C:\Users\rukwaropaul\Downloads\DEV\Projects\02-legacy-dotnet-modernization\modern\tests\Northstar.IntegrationTests\bin\Release\net10.0\Northstar.IntegrationTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:     8, Skipped:     0, Total:     8, Duration: 3 s - Northstar.ModernizationTests.dll (net10.0)

Passed!  - Failed:     0, Passed:    11, Skipped:     0, Total:    11, Duration: 5 s - Northstar.IntegrationTests.dll (net10.0)
```

## Aggregate

| Suite | Passed | Failed | Skipped |
|---|---:|---:|---:|
| Legacy characterization | 7 | 0 | 0 |
| Modern domain/unit | 29 | 0 | 0 |
| Shared modernization characterization/import | 8 | 0 | 0 |
| Modern HTTP integration | 11 | 0 | 0 |
| **Total** | **55** | **0** | **0** |
