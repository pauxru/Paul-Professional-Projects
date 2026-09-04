# INC-010 — Cache stampede

## Symptoms
Many concurrent requests miss the same key and all hit the origin. Cache hit rate can look normal between expiry events, while origin request count spikes sharply at synchronized expiration.

## Business impact
A cold or expired shipment-status key can produce a burst of identical carrier/API/database calls. The origin may time out, triggering retries and spreading a local cache miss into a dependency incident.

## Reproduction
```powershell
dotnet run --project src\Lab.Harness -c Release -- --scenario INC-010 --mode broken --requests 20
dotnet run --project src\Lab.Harness -c Release -- --scenario INC-010 --mode fixed --requests 20
```

## Telemetry & evidence
See [`evidence\inc-010-broken.json`](evidence/inc-010-broken.json) and [`evidence\inc-010-fixed.json`](evidence/inc-010-fixed.json). The 20 callers begin on an empty key.

| Run | Origin loads | Coalesced waiters | Distinct values | p95 caller latency |
|---|---:|---:|---:|---:|
| Broken | 20 | 0 | 1 | 36.68 ms |
| Fixed | 1 | 19 | 1 | 82.30 ms |

Counter/log excerpt:
```text
INC-010 Broken: 63.09 ms; ... originLoads=20
INC-010 Fixed: 108.34 ms; ... originLoads=1
```
The fixed p95 is not claimed as an improvement in this fresh-process run; JIT/scheduling dominates this tiny synthetic timing. The causal signal is actual origin loads: **20 → 1**.

## Hypotheses considered and eliminated
- **Different cached value:** both modes returned one distinct shipment-state value.
- **A cache hit masking work:** every caller starts against an expired/absent key.
- **A request-count bug:** `OutboundCallCounter` increments inside the origin loader itself.

## Root cause
Broken mode checks an empty cache and independently starts an origin load in every concurrent caller before any caller can store the value.

## The fix
```diff
- if (!cache.TryGetValue(key, out value))
-     value = await LoadOriginAsync(key, ct);
+ var singleFlight = inflight.GetOrAdd(key,
+     k => new Lazy<Task<CacheEntry>>(() => LoadOriginAsync(k, ct)));
+ value = (await singleFlight.Value).Value; // TTL has per-key jitter
```

## Verification
The real counter changed from **20 origin loads to 1**, coalescing **19** waiters. The test requires broken mode to load the origin at least five times more often than fixed mode and uses a bounded 40-caller maximum.

## Prevention
Measure origin loads per cache key/miss group, coalesce refreshes, jitter TTLs, consider stale-while-revalidate, impose cache-size limits, and test an empty-key concurrent burst in CI.

## Related failure modes
Synchronized TTL expiry, cache invalidation fan-out, cache eviction under memory pressure, distributed-lock failure, and retry storms against the origin commonly combine with a stampede.
