# Test and Validation Results

## Context

- Date: 2026-09-03
- Host: Windows
- .NET SDK: 10.0.400
- Azure CLI: 2.86 (provided host fact)
- Bicep CLI: 0.44.1 (`28275db947`)
- API port: 5028

## Release build — passed

Command:

```powershell
dotnet build -c Release
```

Real final output:

```text
  Determining projects to restore...
  All projects are up-to-date for restore.
  Contoso.Storefront.Domain -> C:\Users\rukwaropaul\Downloads\DEV\Projects\28-cloud-deployment-reference\src\Contoso.Storefront.Domain\bin\Release\net10.0\Contoso.Storefront.Domain.dll
  Contoso.Storefront.Application -> C:\Users\rukwaropaul\Downloads\DEV\Projects\28-cloud-deployment-reference\src\Contoso.Storefront.Application\bin\Release\net10.0\Contoso.Storefront.Application.dll
  Contoso.Storefront.Infrastructure -> C:\Users\rukwaropaul\Downloads\DEV\Projects\28-cloud-deployment-reference\src\Contoso.Storefront.Infrastructure\bin\Release\net10.0\Contoso.Storefront.Infrastructure.dll
  Contoso.Storefront.MigrationRunner -> C:\Users\rukwaropaul\Downloads\DEV\Projects\28-cloud-deployment-reference\src\Contoso.Storefront.MigrationRunner\bin\Release\net10.0\Contoso.Storefront.MigrationRunner.dll
  Contoso.Storefront.Api -> C:\Users\rukwaropaul\Downloads\DEV\Projects\28-cloud-deployment-reference\src\Contoso.Storefront.Api\bin\Release\net10.0\Contoso.Storefront.Api.dll
  Contoso.Storefront.UnitTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\28-cloud-deployment-reference\tests\Contoso.Storefront.UnitTests\bin\Release\net10.0\Contoso.Storefront.UnitTests.dll
  Contoso.Storefront.IntegrationTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\28-cloud-deployment-reference\tests\Contoso.Storefront.IntegrationTests\bin\Release\net10.0\Contoso.Storefront.IntegrationTests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:04.31
```

## Release tests — passed

Command:

```powershell
dotnet test -c Release --logger "console;verbosity=minimal"
```

Real final summaries:

```text
  Determining projects to restore...
  All projects are up-to-date for restore.
  Contoso.Storefront.Domain -> C:\Users\rukwaropaul\Downloads\DEV\Projects\28-cloud-deployment-reference\src\Contoso.Storefront.Domain\bin\Release\net10.0\Contoso.Storefront.Domain.dll
  Contoso.Storefront.Application -> C:\Users\rukwaropaul\Downloads\DEV\Projects\28-cloud-deployment-reference\src\Contoso.Storefront.Application\bin\Release\net10.0\Contoso.Storefront.Application.dll
  Contoso.Storefront.Infrastructure -> C:\Users\rukwaropaul\Downloads\DEV\Projects\28-cloud-deployment-reference\src\Contoso.Storefront.Infrastructure\bin\Release\net10.0\Contoso.Storefront.Infrastructure.dll
  Contoso.Storefront.Api -> C:\Users\rukwaropaul\Downloads\DEV\Projects\28-cloud-deployment-reference\src\Contoso.Storefront.Api\bin\Release\net10.0\Contoso.Storefront.Api.dll
  Contoso.Storefront.UnitTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\28-cloud-deployment-reference\tests\Contoso.Storefront.UnitTests\bin\Release\net10.0\Contoso.Storefront.UnitTests.dll
  Contoso.Storefront.IntegrationTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\28-cloud-deployment-reference\tests\Contoso.Storefront.IntegrationTests\bin\Release\net10.0\Contoso.Storefront.IntegrationTests.dll
Test run for C:\Users\rukwaropaul\Downloads\DEV\Projects\28-cloud-deployment-reference\tests\Contoso.Storefront.UnitTests\bin\Release\net10.0\Contoso.Storefront.UnitTests.dll (.NETCoreApp,Version=v10.0)
Test run for C:\Users\rukwaropaul\Downloads\DEV\Projects\28-cloud-deployment-reference\tests\Contoso.Storefront.IntegrationTests\bin\Release\net10.0\Contoso.Storefront.IntegrationTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    50, Skipped:     0, Total:    50, Duration: 1 s - Contoso.Storefront.UnitTests.dll (net10.0)

Passed!  - Failed:     0, Passed:    16, Skipped:     0, Total:    16, Duration: 2 s - Contoso.Storefront.IntegrationTests.dll (net10.0)
```

Aggregate: **66 passed, 0 failed, 0 skipped**.

## EF migration smoke check — passed

The migration runner was executed against a disposable local SQLite file. It applied all six migrations and reported:

```text
Applying 6 migration(s): 20260903010000_InitialSchema, 20260903011000_ExpandProductDescription, 20260903012000_BackfillProductDescription, 20260903013000_DualWriteProductDescription, 20260903014000_SwitchProductDescriptionReads, 20260903015000_ContractProductDescription
Migration runner completed successfully; database is current.
```

The disposable database was removed. Automated tests also prove an immediate second migration run is idempotent.

## Bicep syntax checks — passed

Commands:

```powershell
az bicep version
az bicep build --file infra\bicep\main.bicep
az bicep build-params --file infra\bicep\environments\dev.bicepparam
az bicep build-params --file infra\bicep\environments\staging.bicepparam
az bicep build-params --file infra\bicep\environments\prod.bicepparam
```

Real version output:

```text
Bicep CLI version 0.44.1 (28275db947)
```

The final main build and all three parameter builds exited with code 0 and produced no template/linter diagnostic. Azure CLI printed only:

```text
WARNING: A new Bicep release is available: v0.46.1. Upgrade now by running "az bicep upgrade".
```

Generated JSON was deleted after validation. This proves local Bicep compilation only. No Azure What-If, policy evaluation or deployment was executed.

## Local canary simulation — passed

Healthy run:

```text
Green weight 5%: Promote — Error-rate and latency gates are healthy.
Green weight 20%: Promote — Error-rate and latency gates are healthy.
Green weight 50%: Promote — Error-rate and latency gates are healthy.
Green weight 100%: Promote — Error-rate and latency gates are healthy.
Simulation promoted green to 100%.
```

Injected candidate failure:

```text
Green weight 5%: Promote — Error-rate and latency gates are healthy.
Green weight 20%: Rollback — Error rate 20% breached the gate.
Simulation routed traffic back to blue (100%).
```

The script started two local API instances on ports 5028 and 5128, queried readiness and metrics, shifted synthetic traffic, then stopped both processes.

## Authored but not executed

- Terraform: no `fmt`, `init`, `validate`, `plan` or `apply`; the binary is unavailable.
- Dockerfile/docker-compose: Docker is unavailable; no image or container was built.
- GitHub Actions: workflow YAML was authored but not executed.
- Azure: no subscription was used, no What-If ran and **no Azure resource was provisioned**.
- PostgreSQL, SQL Server, Redis and Service Bus managed adapters were not connected to real services.

These limitations are intentional and are not represented as successful validation.
