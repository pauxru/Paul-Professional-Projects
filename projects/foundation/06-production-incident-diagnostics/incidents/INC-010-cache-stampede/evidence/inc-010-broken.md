# INC-010 — Cache stampede on concurrent miss (Broken)

- Started (UTC): 2026-09-02T22:34:27.6237733+00:00
- Requested operations: 20
- Elapsed: 63.09 ms
- Completed within budget: True

## Metrics

- **originLoads:** 20
- **coalescedWaiters:** 0
- **p95LatencyMilliseconds:** 36.68
- **distinctValuesReturned:** 1
- **ttlPolicy:** 200 ms base TTL plus deterministic 0-24 ms jitter

## Evidence

- OutboundCallCounter observed 20 origin loads for 20 simultaneous cache callers.
- Fixed mode uses ConcurrentDictionary<string, Lazy<Task<CacheEntry>>> execution-and-publication coalescing.

## Limitations

- Single-flight state is process-local. A multi-node cache needs a distributed lock or a stale-while-revalidate strategy to prevent cross-node stampedes.
