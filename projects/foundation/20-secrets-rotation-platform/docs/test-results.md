# Test Results

Verified on 2026-09-03 on Windows with .NET SDK 10.0.400. No Docker, cloud resource,
PostgreSQL, Redis, or external service was used.

## Release build

Command:

```powershell
dotnet build -c Release
```

Real output:

```text
  Determining projects to restore...
  All projects are up-to-date for restore.
  Northstar.Secrets.Domain -> ...\Northstar.Secrets.Domain.dll
  Northstar.Secrets.ConsumerSample -> ...\Northstar.Secrets.ConsumerSample.dll
  Northstar.Secrets.Application -> ...\Northstar.Secrets.Application.dll
  Northstar.Secrets.Infrastructure -> ...\Northstar.Secrets.Infrastructure.dll
  Northstar.Secrets.UnitTests -> ...\Northstar.Secrets.UnitTests.dll
  Northstar.Secrets.Api -> ...\Northstar.Secrets.Api.dll
  Northstar.Secrets.IntegrationTests -> ...\Northstar.Secrets.IntegrationTests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:10.19
```

Paths were shortened only with `...`; counts and outcome are copied from the real command.

## Release tests

Command:

```powershell
dotnet test -c Release
```

Real output:

```text
Passed!  - Failed:     0, Passed:    50, Skipped:     0, Total:    50, Duration: 4 s - Northstar.Secrets.UnitTests.dll (net10.0)

Passed!  - Failed:     0, Passed:     7, Skipped:     0, Total:     7, Duration: 7 s - Northstar.Secrets.IntegrationTests.dll (net10.0)
```

Aggregate result: **57 passed, 0 failed, 0 skipped**.

## End-to-end smoke demonstration

The API was started at `http://localhost:5020`, `/health/ready` returned HTTP 200, and
`scripts\demo.ps1` was executed. The real observed workflow outcomes were:

```text
Dual-write rotation after consumer acknowledgement: Completed
Simulated verification-failure rotation: RolledBack
Demo completed: one successful rotation and one verified rollback.
```

## Coverage map

| Required behavior | Test evidence |
|---|---|
| AES-GCM round trip, tamper, AAD transplant | `CryptographyTests` |
| Master-key DEK re-wrap | `CryptographyTests` |
| Version states and two-active window | `DomainLifecycleTests` |
| Password/API/keypair/certificate generators | `GeneratorTests` |
| Both rotation strategies | `RotationEngineTests` |
| Verification and acknowledgement rollback | `RotationEngineTests` |
| Crash/resume and idempotency | `RotationEngineTests` |
| Jitter over 200 secrets and expiry | `SchedulingAndSecurityTests` |
| Emergency revoke and four-eyes | `SchedulingAndSecurityTests` |
| Path RBAC and read audit | `SchedulingAndSecurityTests` |
| Log leak scanning | `SchedulingAndSecurityTests` |
| Webhook signature, retry, DLQ | `NotificationAndAdapterTests` |
| API validation, 401, 403, SQLite HTTP path | `ApiContractTests` |
