# Runbook: Memory and Worker Exhaustion

## Scope
Use for heap growth, GC pauses, request queue growth, unavailable ThreadPool workers, or connection-lease timeouts. Relevant lab incidents: `INC-003` through `INC-006`.

## Immediate safeguards
- Reduce concurrency or shed nonessential work before collecting heavier diagnostics.
- Choose one canary instance and an abort deadline.
- Do not globally change ThreadPool min/max values during diagnosis unless an approved mitigation requires it.
- Preserve a sample of correlation IDs and deployment metadata.

## Diagnose memory
1. Compare allocation rate with post-full-GC heap size.
2. Inspect static caches, event subscriptions, timers, singleton collections, and closures retaining request objects.
3. Use a short trace or GC dump only after checking capture cost and artifact handling.
4. Prove the root is released in a bounded regression test.

## Diagnose worker starvation
1. Inspect completed rate, p95/p99, ThreadPool queue length, available workers, and blocking stack frames.
2. Search request paths for sync-over-async, blocking I/O, lock contention, and unbounded `Task.Run`.
3. Measure queue delay from enqueue to work start.
4. Isolate long/blocking work behind a bounded channel or dedicated worker scheduler.

## Verification
The fix is successful when queue delay and p95 recover under comparable load and the resource signal stops worsening. Do not claim success from a restart alone.

## Lab command
```powershell
dotnet run --project src\Lab.Harness -c Release -- --scenario INC-004 --mode broken --requests 20
dotnet run --project src\Lab.Harness -c Release -- --scenario INC-006 --mode fixed --requests 20
```
