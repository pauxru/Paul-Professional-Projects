# INC-006 — Thread-pool starvation from blocking work (Fixed)

- Started (UTC): 2026-09-02T22:35:06.1099379+00:00
- Requested operations: 20
- Elapsed: 211.22 ms
- Completed within budget: True

## Metrics

- **queueDelayP50Milliseconds:** 44.36
- **queueDelayP95Milliseconds:** 90.22
- **queueDelayP99Milliseconds:** 90.28
- **workerModel:** bounded Channel + 4 dedicated long-running workers
- **peakBusyWorkerThreads:** 0
- **boundedConcurrency:** 4

## Evidence

- Queue delay is measured from enqueue timestamp to the moment a work item starts consuming a worker slot.
- Fixed mode runs blocking work behind a bounded channel on dedicated long-running workers, preventing request workers from being held.

## Limitations

- The capacity gate makes the starvation signal repeatable without changing process-wide ThreadPool limits.
