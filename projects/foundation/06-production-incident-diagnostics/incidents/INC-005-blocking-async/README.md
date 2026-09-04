# INC-005 — Blocking async / sync-over-async

## Symptoms
Tail latency grows while available ThreadPool workers fall or work queues build. Thread stacks show synchronous waits (`.Wait()` or `.Result`) in request code even though the underlying operation is asynchronous.

## Business impact
Each blocked request worker lowers the service’s ability to accept unrelated work. Under load this can appear as random timeouts, delayed health checks, and low CPU despite a saturated request queue.

## Reproduction
```powershell
dotnet run --project src\Lab.Harness -c Release -- --scenario INC-005 --mode broken --requests 20
dotnet run --project src\Lab.Harness -c Release -- --scenario INC-005 --mode fixed --requests 20
```

## Telemetry & evidence
See [`evidence\inc-005-broken.json`](evidence/inc-005-broken.json) and [`evidence\inc-005-fixed.json`](evidence/inc-005-fixed.json). The same 40 ms I/O-shaped delay is used in both paths.

| Run | Completed | Requests/sec | Peak busy workers | Minimum available workers | Worker-blocked time |
|---|---:|---:|---:|---:|---:|
| Broken | 20 | 150.11 | 21 | 32,746 | 1,084 ms |
| Fixed | 20 | 164.15 | 2 | 32,765 | 0 ms |

Captured harness excerpt:
```text
INC-005 Broken: 133.24 ms; evidence: ...\inc-005-broken.json
INC-005 Fixed: 121.84 ms; evidence: ...\inc-005-fixed.json
```
The report records 40 ThreadPool samples in each mode. Absolute worker counts are host-sensitive; the measured blocked-worker total is the stable causal signal.

## Hypotheses considered and eliminated
- **CPU-bound work:** the workload is a delay, not computation.
- **Slow downstream response alone:** both modes await the same 40 ms simulated operation.
- **An unbounded starvation test:** requests are capped at 32 and a cancellation budget terminates all waits.

## Root cause
Broken mode calls `Task.Delay(...).Wait(...)` from a `Task.Run` request worker, tying up a worker while the asynchronous operation is pending.

## The fix
```diff
- Task.Delay(TimeSpan.FromMilliseconds(40), ct).Wait(ct);
+ await Task.Delay(TimeSpan.FromMilliseconds(40), ct);
```

## Verification
Real evidence shows **1,084 ms** of aggregate worker-blocked time in broken mode and **0 ms** after the `await` change; peak busy workers moved from **21 to 2** in this run. The incident test asserts the measured blocking total changes, while preserving a hard deadline.

## Prevention
Ban sync-over-async on request paths in review and analyzers, propagate `CancellationToken`, inspect ThreadPool availability/queue counters, and load-test async dependency paths. Do not use a global min-thread increase as the primary fix.

## Related failure modes
Blocking database/network calls, lock contention, synchronous logging sinks, `.Result` in constructors, and request code that performs long CPU work on the pool have similar signatures.
