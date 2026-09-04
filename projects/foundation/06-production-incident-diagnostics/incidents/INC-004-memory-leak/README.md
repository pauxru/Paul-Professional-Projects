# INC-004 — Static memory retention leak

## Symptoms
Managed heap size remains elevated after full GC, Gen 2 activity rises over time, and a process restarts temporarily relieve memory pressure. Heap inspection often points to singleton collections, event handlers, timers, or delegates retaining request data.

## Business impact
Retained payloads inflate memory, increase GC pause risk, reduce density, and can end in out-of-memory termination. The impact accumulates rather than appearing as a single failed request.

## Reproduction
```powershell
dotnet run --project src\Lab.Harness -c Release -- --scenario INC-004 --mode broken --requests 20
dotnet run --project src\Lab.Harness -c Release -- --scenario INC-004 --mode fixed --requests 20
```

## Telemetry & evidence
Real evidence: [`evidence\inc-004-broken.json`](evidence/inc-004-broken.json) and [`evidence\inc-004-fixed.json`](evidence/inc-004-fixed.json). Each operation allocates a bounded 32 KiB payload.

| Run | Retained payloads | Static handlers | Heap delta after forced GC | Allocated bytes |
|---|---:|---:|---:|---:|
| Broken | 20 | 20 | 650,672 | 653,208 |
| Fixed | 0 | 0 | 25,464 | 649,264 |

Counter excerpt:
```text
GC.GetTotalMemory(true) delta: broken 650672 bytes; fixed 25464 bytes
GC.GetTotalAllocatedBytes delta: broken 653208 bytes; fixed 649264 bytes
```

## Hypotheses considered and eliminated
- **Normal allocation churn:** allocated bytes are similar, but only broken mode retains payloads after forced GC.
- **A one-off runtime cache:** the deterministic static-handler and payload counters grow exactly with requests.
- **An unbounded test artifact:** the scenario caps work at 80 iterations and clears static state in `finally`.

## Root cause
The broken path stores each payload in a static list and captures it in a static event-handler subscription. Neither root is removed during request completion.

## The fix
```diff
- Payloads.Add(payload);
- _pulse += (_, _) => GC.KeepAlive(payload);
+ await ProcessAndReleaseAsync(payload, ct);
+ // no static collection or event subscription owns request data
```

## Verification
The real fixed run retained **0** payloads and handlers, versus **20** in broken mode. Its post-GC heap delta was **25,464 bytes** rather than **650,672 bytes**. Static state is cleared after either mode to protect the surrounding process.

## Prevention
Audit static collections, singleton callbacks, events, timer registrations, and closures. Alert on post-GC heap trend and validate suspected paths with a GC dump under controlled conditions. Add a retention-count regression test when a cache or subscription is introduced.

## Related failure modes
Unbounded caches, `CancellationToken.Register` registrations not disposed, `Timer` callbacks, event aggregators, background-service closures, and native resource wrappers can produce similar retention symptoms.
