# INC-005 — Sync-over-async request blocking (Fixed)

- Started (UTC): 2026-09-02T22:37:27.6516570+00:00
- Requested operations: 20
- Elapsed: 121.84 ms
- Completed within budget: True

## Metrics

- **completedRequests:** 20
- **requestsPerSecond:** 164.15
- **p95LatencyMilliseconds:** 97.62
- **peakBusyWorkerThreads:** 2
- **minimumAvailableWorkerThreads:** 32765
- **threadPoolSampleCount:** 40
- **blockedWorkerMilliseconds:** 0
- **syncWaitUsed:** False

## Evidence

- ThreadPool.GetAvailableThreads was sampled at request entry and exit.
- Broken mode calls Task.Delay(...).Wait(...) on ThreadPool work items; fixed mode awaits the same delay.

## Limitations

- Thread-pool injection is runtime and host dependent. The robust signal is occupied worker threads, not an absolute requests-per-second target.
