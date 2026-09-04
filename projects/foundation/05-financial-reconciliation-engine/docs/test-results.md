# Test Results

> This file contains the **real, pasted output** of `dotnet build -c Release` and
> `dotnet test -c Release` run on the build host. Nothing here is hand-edited beyond trimming the
> absolute restore paths for readability.

- **Host**: Microsoft Windows 10.0.26200, 16 logical cores, .NET SDK 10.0.400 (`net10.0`).
- **Result**: build **0 warnings / 0 errors**; tests **120 passed, 0 failed, 0 skipped**
  (105 unit + 15 integration).

## `dotnet build -c Release`

```text
Determining projects to restore...
All projects are up-to-date for restore.
ReconEngine.Domain -> ...\src\ReconEngine.Domain\bin\Release\net10.0\ReconEngine.Domain.dll
ReconEngine.Application -> ...\src\ReconEngine.Application\bin\Release\net10.0\ReconEngine.Application.dll
ReconEngine.DataGen -> ...\src\ReconEngine.DataGen\bin\Release\net10.0\ReconEngine.DataGen.dll
ReconEngine.Infrastructure -> ...\src\ReconEngine.Infrastructure\bin\Release\net10.0\ReconEngine.Infrastructure.dll
ReconEngine.PerfHarness -> ...\src\ReconEngine.PerfHarness\bin\Release\net10.0\ReconEngine.PerfHarness.dll
ReconEngine.UnitTests -> ...\tests\ReconEngine.UnitTests\bin\Release\net10.0\ReconEngine.UnitTests.dll
ReconEngine.Api -> ...\src\ReconEngine.Api\bin\Release\net10.0\ReconEngine.Api.dll
ReconEngine.IntegrationTests -> ...\tests\ReconEngine.IntegrationTests\bin\Release\net10.0\ReconEngine.IntegrationTests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:02.78
```

## `dotnet test -c Release`

```text
Test run for ...\tests\ReconEngine.UnitTests\bin\Release\net10.0\ReconEngine.UnitTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.
Test run for ...\tests\ReconEngine.IntegrationTests\bin\Release\net10.0\ReconEngine.IntegrationTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:   105, Skipped:     0, Total:   105, Duration: 419 ms - ReconEngine.UnitTests.dll (net10.0)

Passed!  - Failed:     0, Passed:    15, Skipped:     0, Total:    15, Duration: 15 s - ReconEngine.IntegrationTests.dll (net10.0)
```

## Test breakdown

The unit total (105) is larger than the number of test methods below because several tests are
xUnit `[Theory]` cases that expand into multiple data rows (tolerance boundaries, status-contradiction
pairs, currency-decimals, etc.).

### Unit tests — `ReconEngine.UnitTests` (105 passed)

| Area | File | Test methods |
|------|------|-------------:|
| Matching rules | `Matching/MatchingRuleTests.cs` | 18 |
| Domain values (`Money`, `CurrencyInfo`, canonicaliser, row hash) | `Domain/DomainValueTests.cs` | 17 |
| Reconciliation (dedup, balance, status, classifier, multi-currency) | `Reconciliation/ReconciliationTests.cs` | 11 |
| Record mapping edge cases | `Ingestion/RecordMapperTests.cs` | 11 |
| Exception workflow + four-eyes | `Workflow/ExceptionWorkflowTests.cs` | 10 |
| CSV export injection / RFC-4180 | `Export/CsvWriterTests.cs` | 9 |
| CSV streaming tokenizer edge cases | `Ingestion/CsvRowTokenizerTests.cs` | 8 |
| Generator exact-count invariant | `DataGeneration/GeneratorExactCountsTests.cs` | 1 |

### Integration tests — `ReconEngine.IntegrationTests` (15 passed)

| Area | File | Test methods |
|------|------|-------------:|
| API happy path / validation / 401 / 403 / import | `Api/ApiEndpointTests.cs` | 9 |
| End-to-end reconciliation flow (manifest counts, idempotent re-run, carry-forward, 50k streaming) | `Reconciliation/ReconciliationFlowTests.cs` | 4 |
| Workflow persistence via HTTP (assign→comment→resolve; four-eyes write-off approval) | `Workflow/ExceptionWorkflowApiTests.cs` | 2 |

> The two `ExceptionWorkflowApiTests` drive the manual-resolution workflow through the real HTTP API
> and the EF Core + SQLite persistence layer. They were added after an end-to-end demo run surfaced a
> defect that pure-domain unit tests could not reach: a new audit entry / comment appended to an
> *already-tracked* exception aggregate was being mistaken for an existing row (client-generated Guid
> key already set + the default "store-generated key" convention) and emitted as an `UPDATE` instead of
> an `INSERT`, failing the optimistic-concurrency check on **every** persisted transition. The fix
> declares the client-generated Guid keys `ValueGeneratedNever()` (see
> `src/ReconEngine.Infrastructure/Persistence/AppDbContext.cs`); these tests pin the behaviour so it
> cannot regress.

## Coverage of the required test matrix

- ✅ Every matching rule in isolation with hand-built fixtures.
- ✅ Each exception class detected with an exact expected count from the generator manifest.
- ✅ Tolerance boundary tests (just inside / just outside), including the percentage basis being the
  larger amount.
- ✅ Many-to-one and one-to-many (bounded subset-sum).
- ✅ Fee-adjusted matching and fee variance.
- ✅ Refund pairing to an original capture.
- ✅ Duplicate detection by row hash.
- ✅ Multi-currency isolation (never match across currencies).
- ✅ Idempotent re-run (no duplicate exceptions; total open set stable).
- ✅ Carry-forward across two runs.
- ✅ Balance assertion.
- ✅ Four-eyes approval enforcement (same-user approval/rejection rejected).
- ✅ Workflow persistence through the HTTP API + EF Core/SQLite (assign→comment→resolve; write-off
  four-eyes approval by a second reviewer; proposer self-approval rejected with `409 Conflict`).
- ✅ Resolution state machine (legal + illegal transitions).
- ✅ CSV / fixed-width parser edge cases (quoted commas, embedded newlines, BOM, blank lines, bad
  dates, negative / zero amounts).
- ✅ API happy path, validation (`ProblemDetails`/400), 401 and 403.
- ✅ Large-file streaming test (50k rows) asserting completion within a sane bound.

## End-to-end demo verification

Beyond the automated suite, `scripts/demo.ps1` was executed against a fresh SQLite database on the
build host (PowerShell 7.6, 5 000-row seed-42 dataset). All nine stages completed:

```text
5/9  Import  : internal 5050 accepted / 0 rejected, external 5020 accepted / 0 rejected
6/9  Run     : matches=4930  exceptions=195  carriedForward=0  balanceOK=True  1462ms
7/9  Report  : balanceAssertionPassed=true (per-currency internal = matched + unmatched)
8/9  Triage  : assign -> comment -> resolve  =>  status after maker resolve: Resolved (audit trail written)
9/9  OpenAPI : http://localhost:5005/openapi/v1.json
Demo complete.
```

The first execution of this demo is what surfaced the workflow-persistence concurrency defect described
above; after the fix the full assign → comment → resolve path (and, in the automated tests, the
four-eyes write-off approval) completes successfully.

## Reproduce

```powershell
cd 05-financial-reconciliation-engine
dotnet build -c Release
dotnet test  -c Release
```
