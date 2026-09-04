# INC-005 — Sync-over-async request blocking (Broken)

- Started (UTC): 2026-09-02T22:37:25.3603682+00:00
- Requested operations: 20
- Elapsed: 133.24 ms
- Completed within budget: True

## Metrics

- **completedRequests:** 20
- **requestsPerSecond:** 150.11
- **p95LatencyMilliseconds:** 73.38
- **peakBusyWorkerThreads:** 21
- **minimumAvailableWorkerThreads:** 32746
- **threadPoolSampleCount:** 40
- **blockedWorkerMilliseconds:** 1084
- **syncWaitUsed:** True

## Evidence

- ThreadPool.GetAvailableThreads was sampled at request entry and exit.
- Broken mode calls Task.Delay(...).Wait(...) on ThreadPool work items; fixed mode awaits the same delay.

## Limitations

- Thread-pool injection is runtime and host dependent. The robust signal is occupied worker threads, not an absolute requests-per-second target.
