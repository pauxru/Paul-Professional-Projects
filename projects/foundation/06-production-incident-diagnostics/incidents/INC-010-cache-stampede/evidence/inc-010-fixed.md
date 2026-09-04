# INC-010 — Cache stampede on concurrent miss (Fixed)

- Started (UTC): 2026-09-02T22:35:27.5808944+00:00
- Requested operations: 20
- Elapsed: 108.34 ms
- Completed within budget: True

## Metrics

- **originLoads:** 1
- **coalescedWaiters:** 19
- **p95LatencyMilliseconds:** 82.30
- **distinctValuesReturned:** 1
- **ttlPolicy:** 200 ms base TTL plus deterministic 0-24 ms jitter

## Evidence

- OutboundCallCounter observed 1 origin loads for 20 simultaneous cache callers.
- Fixed mode uses ConcurrentDictionary<string, Lazy<Task<CacheEntry>>> execution-and-publication coalescing.

## Limitations

- Single-flight state is process-local. A multi-node cache needs a distributed lock or a stale-while-revalidate strategy to prevent cross-node stampedes.
