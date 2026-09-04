# Test results

> This file contains the **real** output from running `dotnet build -c Release` and
> `dotnet test -c Release` on the build host. Rerun with the commands shown to reproduce.

## Environment

- .NET SDK: **10.0.400**
- Target framework: `net10.0`
- Operating system: Windows
- Test framework: xUnit

## Build

Command:
```
dotnet build -c Release
```

Tail of output:
```
Healthcare.Domain -> ...\src\Healthcare.Domain\bin\Release\net10.0\Healthcare.Domain.dll
Healthcare.Application -> ...\src\Healthcare.Application\bin\Release\net10.0\Healthcare.Application.dll
Healthcare.Infrastructure -> ...\src\Healthcare.Infrastructure\bin\Release\net10.0\Healthcare.Infrastructure.dll
Healthcare.UnitTests -> ...\tests\Healthcare.UnitTests\bin\Release\net10.0\Healthcare.UnitTests.dll
Healthcare.Api -> ...\src\Healthcare.Api\bin\Release\net10.0\Healthcare.Api.dll
Healthcare.IntegrationTests -> ...\tests\Healthcare.IntegrationTests\bin\Release\net10.0\Healthcare.IntegrationTests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:03.83
```

## Tests

Command:
```
dotnet test -c Release --no-build
```

Tail of output:
```
Test run for ...\tests\Healthcare.UnitTests\bin\Release\net10.0\Healthcare.UnitTests.dll (.NETCoreApp,Version=v10.0)
Test run for ...\tests\Healthcare.IntegrationTests\bin\Release\net10.0\Healthcare.IntegrationTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    29, Skipped:     0, Total:    29, Duration: 110 ms - Healthcare.UnitTests.dll (net10.0)

Passed!  - Failed:     0, Passed:    33, Skipped:     0, Total:    33, Duration: 1 s - Healthcare.IntegrationTests.dll (net10.0)
```

## Summary

| Suite                          | Passed | Failed | Skipped | Total |
| ------------------------------ | ------ | ------ | ------- | ----- |
| `Healthcare.UnitTests`         | 29     | 0      | 0       | 29    |
| `Healthcare.IntegrationTests`  | 33     | 0      | 0       | 33    |
| **Overall**                    | **62** | **0**  | **0**   | **62**|

## Integration-test coverage map

| Test class                         | Tests | Signature behaviour covered                                  |
| ---------------------------------- | ----- | ------------------------------------------------------------- |
| `ApiSurfaceTests`                  | 4     | Health, dev-token, 401, list facilities.                     |
| `AvailabilityTests`                | 3     | Slot search, weekend exclusion, DST correctness.             |
| `AppointmentLifecycleTests`        | 4     | Reschedule, cancel-then-rebook, state machine, 422.          |
| `ConcurrentBookingTests`           | 1     | Two parallel bookings → exactly one succeeds.                |
| `WaitlistTests`                    | 2     | Auto-offer on cancel, offer expiry.                          |
| `ClinicalNotesTests`               | 3     | Append-only, amendment versions, vitals unit 422.            |
| `AccessControlTests`               | 7     | RBAC / ABAC / break-glass / audit / 401.                     |
| `ReminderTests`                    | 4     | Idempotency, dispatch, opt-out, confirmation.                |
| `ReferralAndRegistrationTests`     | 4     | Duplicate detection, non-duplicate, SLA breach, before-breach. |
| `AuditIntegrationTests`            | 1     | (implicit, covered by access-control tests)                  |
