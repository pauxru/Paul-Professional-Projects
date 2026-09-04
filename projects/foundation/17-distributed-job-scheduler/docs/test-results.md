# Test Results

Real, unedited output from the two Definition-of-Done gates on the build host.
Re-run at any time with the commands shown; both must exit `0`.

- **Host:** Windows, .NET SDK **10.0.400**, target framework `net10.0`
- **External infrastructure:** none (EF Core + SQLite, file-based for integration tests)
- **Assertion style:** plain xUnit `Assert` only (FluentAssertions is not used)
- **Totals:** **186 passed, 0 failed, 0 skipped** (155 unit + 31 integration)

> Note: the two test projects report **186** executed tests from **130** `[Fact]`/`[Theory]`
> methods — several `[Theory]` cases expand into many `[InlineData]` rows (for example the
> cron-parser and trigger-schedule theories), so the executed-test count is higher than the
> method count.

---

## 1. `dotnet build -c Release`

Command:

```powershell
dotnet build JobScheduler.slnx -c Release
```

Output (tail):

```
Determining projects to restore...
  All projects are up-to-date for restore.
  JobScheduler.Domain -> ...\src\JobScheduler.Domain\bin\Release\net10.0\JobScheduler.Domain.dll
  JobScheduler.Application -> ...\src\JobScheduler.Application\bin\Release\net10.0\JobScheduler.Application.dll
  JobScheduler.Infrastructure -> ...\src\JobScheduler.Infrastructure\bin\Release\net10.0\JobScheduler.Infrastructure.dll
  JobScheduler.Worker -> ...\src\JobScheduler.Worker\bin\Release\net10.0\JobScheduler.Worker.dll
  JobScheduler.UnitTests -> ...\tests\JobScheduler.UnitTests\bin\Release\net10.0\JobScheduler.UnitTests.dll
  JobScheduler.Api -> ...\src\JobScheduler.Api\bin\Release\net10.0\JobScheduler.Api.dll
  JobScheduler.IntegrationTests -> ...\tests\JobScheduler.IntegrationTests\bin\Release\net10.0\JobScheduler.IntegrationTests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:02.85
```

**Result: build succeeded — 0 warnings, 0 errors.**

---

## 2. `dotnet test -c Release`

Command:

```powershell
dotnet test JobScheduler.slnx -c Release --no-build
```

Output:

```
Test run for ...\tests\JobScheduler.UnitTests\bin\Release\net10.0\JobScheduler.UnitTests.dll (.NETCoreApp,Version=v10.0)
Test run for ...\tests\JobScheduler.IntegrationTests\bin\Release\net10.0\JobScheduler.IntegrationTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:   155, Skipped:     0, Total:   155, Duration: 64 ms - JobScheduler.UnitTests.dll (net10.0)

Passed!  - Failed:     0, Passed:    31, Skipped:     0, Total:    31, Duration: 4 s - JobScheduler.IntegrationTests.dll (net10.0)
```

**Result: all tests passed — 186 total (155 + 31), 0 failed, 0 skipped.**

---

## 3. Test inventory

Counts below are `[Fact]`/`[Theory]` **methods** per file (theory rows expand at run time,
which is why the executed total is 186).

### Unit tests — `tests/JobScheduler.UnitTests` (110 executed)

| File | Methods | Covers |
| --- | --- | --- |
| `Scheduling/CronParserTests.cs` | 14 | fields, ranges, steps, lists, `L`/`#`, invalid expressions, next-N vs hand-computed |
| `Scheduling/CronDstTests.cs` | 4 | spring-forward skipped local time, fall-back repeated local time |
| `Scheduling/TriggerScheduleTests.cs` | 10 | one-off/interval/cron/manual, misfire policies, catch-up window |
| `Domain/JobRunFencingTests.cs` | 10 | claim/start/heartbeat/complete guards, fencing-token monotonicity |
| `Domain/RunStateMachineTests.cs` | 5 | legal/illegal run-state transitions |
| `Domain/RetryPolicyTests.cs` | 7 | fixed / exponential / exponential-jitter / capped backoff sequences |
| `Domain/LeaderLeaseTests.cs` | 6 | acquire / renew / take-over / fencing on the leader lease |
| `Domain/PrioritySchedulerTests.cs` | 5 | priority ordering + aging prevents starvation |
| `Domain/DagValidatorTests.cs` | 6 | topological order, fan-in/fan-out, cycle detection |
| `Domain/TimeZoneResolverTests.cs` | 5 | IANA/Windows id resolution, DST offset selection |
| `Domain/WorkerNodeTests.cs` | 7 | registration, heartbeat age, tag matching, dead-node detection |
| `Application/RetryDeciderTests.cs` | 4 | retry vs dead-letter decision, poison detection |
| `Application/CircuitBreakerTests.cs` | 5 | per-job-type circuit open/half-open/close |
| `Application/ConcurrencyGateTests.cs` | 6 | global / per-definition caps, singleton gate |
| `Application/DependencyResolverTests.cs` | 5 | dependency readiness, fan-in gating |

### Integration tests — `tests/JobScheduler.IntegrationTests` (31 executed)

| File | Methods | Covers |
| --- | --- | --- |
| `Coordination/ClaimRaceTests.cs` | 3 | N concurrent workers race one due job → exactly one winner |
| `Coordination/LeaseReclaimTests.cs` | 3 | stalled lease expires → reclaim; stale write is fenced out |
| `Coordination/LeaderElectionTests.cs` | 4 | single-leader invariant, failover within TTL, fence on take-over |
| `Coordination/TimeoutCancellationTests.cs` | 3 | hard timeout, cooperative cancel propagation |
| `Coordination/RetryDlqTests.cs` | 2 | dead-letter after max attempts + replay |
| `Coordination/RetentionPruneTests.cs` | 2 | retention pruning with `FakeClock` |
| `Coordination/WorkerRegistryTests.cs` | 4 | registration, heartbeat, drain, dead-node detection |
| `Api/ApiTests.cs` | 10 | endpoint happy paths, validation, `401`/`403` policy checks |

All concurrency tests use hard `CancellationTokenSource` timeouts so the suite always terminates.
Integration tests run against a **file-based** SQLite database (unique per test host) with
WAL + `busy_timeout`, so concurrent claim `UPDATE`s serialise rather than throwing `SQLITE_BUSY`.
