# INC-003 — Connection pool exhaustion

## Symptoms
Requests wait for a database connection, then fail after a pool-acquisition timeout. Successful operations may drop while the database itself looks lightly loaded because clients are waiting outside it.

## Business impact
Pool exhaustion turns a small connection lifecycle bug into a broad request failure. Dispatch and customer-service calls can pile up despite the database being healthy.

## Reproduction
```powershell
dotnet run --project src\Lab.Harness -c Release -- --scenario INC-003 --mode broken --requests 20
dotnet run --project src\Lab.Harness -c Release -- --scenario INC-003 --mode fixed --requests 20
```

## Telemetry & evidence
Real evidence: [`evidence\inc-003-broken.json`](evidence/inc-003-broken.json) and [`evidence\inc-003-fixed.json`](evidence/inc-003-fixed.json).

| Run | Capacity | Completed | Acquire timeouts | Leases after cleanup |
|---|---:|---:|---:|---:|
| Broken | 3 | 3 | 17 | 0 |
| Fixed | 3 | 20 | 0 | 0 |

Harness excerpt:
```text
INC-003 Broken: 227.53 ms; evidence: ...\inc-003-broken.json
INC-003 Fixed: 187.09 ms; evidence: ...\inc-003-fixed.json
```
The broken acquisition timeout is a hard **80 ms**, so this demonstration terminates rather than waiting indefinitely.

## Hypotheses considered and eliminated
- **Database query slowness:** every acquired lease runs only `SELECT 1`.
- **Pool size alone:** capacity is three in both modes; disposal discipline is the changed variable.
- **Leaked resources after the test:** `leasedAfterCleanup` is zero in both captured reports.

## Root cause
Broken mode acquires the first three real SQLite connections and retains their lease tokens while concurrent contenders wait. The fixed path uses `await using` around every lease and releases capacity on all normal paths.

## The fix
```diff
- var lease = await pool.AcquireAsync(timeout, ct);
- // retained until the workload ends
+ await using var lease = await pool.AcquireAsync(timeout, ct);
+ await lease.ExecuteProbeAsync(ct);
```

## Verification
With 20 operations and a three-slot pool, successful work changed from **3 to 20** and acquisition timeouts changed from **17 to 0**. The cleanup metric remained zero, proving the bounded demo itself leaves no held lease.

## Prevention
Use scoped DbContexts/connections, `using`/`await using` for readers and transactions, finite pool acquisition timeouts, and pool-wait metrics. Test failure paths, not only the successful query.

## Related failure modes
Undisposed data readers, transaction leaks, connection lifetime coupled to streaming responses, and retry storms can all consume pool capacity.
