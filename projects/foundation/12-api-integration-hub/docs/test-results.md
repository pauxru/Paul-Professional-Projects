# Test Results

## Environment

- Date: 2026-09-03
- OS: Windows
- SDK: .NET SDK 10.0.400
- Configuration: Release
- External infrastructure: none
- Outbound network calls during tests: none; simulator hosts ran in-process through `WebApplicationFactory`

## Build

Command:

```powershell
dotnet build -c Release
```

Actual console summary:

```text
Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:02:02.48
```

## Tests

Command:

```powershell
dotnet test -c Release
```

Actual console summaries:

```text
Passed!  - Failed:     0, Passed:    60, Skipped:     0, Total:    60, Duration: 680 ms - IntegrationHub.UnitTests.dll (net10.0)

Passed!  - Failed:     0, Passed:    17, Skipped:     0, Total:    17, Duration: 7 s - IntegrationHub.IntegrationTests.dll (net10.0)
```

Aggregate: **77 passed, 0 failed, 0 skipped**.

## End-to-end demo verification

Command:

```powershell
.\scripts\demo.ps1
```

Observed result:

```text
Build succeeded.
    0 Warning(s)
    0 Error(s)

Forcing one ERP validation failure...
Run 967f8ba0-2fcb-438e-b8c6-7607d9c6db1d finished with status PartiallySucceeded.
Replaying DLQ item 19b01e42-2cd1-4f81-a5b4-217d6cefa974 with its original idempotency key...
Replay 15eb2660-1465-4ff3-ab6c-b6159c33ea9c status: Succeeded.
```

The identifiers above are from the actual local run and are not stable test fixtures.

## Docker

Docker was unavailable on the build host. `Dockerfile` and `docker-compose.yml` are authored and explicitly unverified.
