# INC-006 — Thread-pool starvation from blocking work

## Symptoms
Queued work starts progressively later, ThreadPool worker availability shrinks, and request tails grow even when the actual work duration is short. A health endpoint may still respond intermittently, confusing a superficial availability check.

## Business impact
Long blocking work competes with request processing. Dispatch updates, callback handlers, and queue acknowledgements can all be delayed because they share workers with slow background activity.

## Reproduction
```powershell
dotnet run --project src\Lab.Harness -c Release -- --scenario INC-006 --mode broken --requests 20
dotnet run --project src\Lab.Harness -c Release -- --scenario INC-006 --mode fixed --requests 20
```

## Telemetry & evidence
See [`evidence\inc-006-broken.json`](evidence/inc-006-broken.json) and [`evidence\inc-006-fixed.json`](evidence/inc-006-fixed.json).

| Run | Worker model | Capacity | Queue p50 | Queue p95 | Queue p99 | Peak busy pool workers |
|---|---|---:|---:|---:|---:|---:|
| Broken | ThreadPool + blocking gate | 2 | 86.79 ms | 196.20 ms | 268.43 ms | 9 |
| Fixed | bounded Channel + dedicated workers | 4 | 44.36 ms | 90.22 ms | 90.28 ms | 0 |

Harness excerpt:
```text
INC-006 Broken: 319.88 ms; evidence: ...\inc-006-broken.json
INC-006 Fixed: 211.22 ms; evidence: ...\inc-006-fixed.json
```

## Hypotheses considered and eliminated
- **A slow dependency:** the workload is bounded 14 ms blocking work with no network call.
- **Database lock contention:** this scenario has no database operation.
- **One thread merely running slowly:** p95/p99 enqueue-to-start delay changes when the worker model changes.

## Root cause
The broken path performs blocking work on ThreadPool request workers and funnels them through a two-slot capacity gate. Queued items hold/compete for request workers while waiting to start.

## The fix
```diff
- Task.Run(() => { capacityGate.Wait(ct); Thread.Sleep(14); });
+ channel.Writer.TryWrite(work);
+ Task.Factory.StartNew(ConsumeDedicatedWorker,
+     TaskCreationOptions.LongRunning); // bounded to four workers
```

## Verification
The real p95 queue delay changed from **196.20 ms to 90.22 ms** for the bounded 20-item run. Fixed mode sampled no busy ThreadPool worker for the blocking consumer because work ran on dedicated long-running workers; the test compares queue delay rather than an absolute host speed.

## Prevention
Keep request handlers asynchronous and short, place unavoidable blocking/CPU work behind a bounded channel or scheduler, measure enqueue-to-start delay, and set explicit queue overflow/backpressure behaviour.

## Related failure modes
Sync-over-async, unbounded `Task.Run`, CPU-heavy serialization, single-threaded actor bottlenecks, and background services sharing a request pool can all starve workers.
