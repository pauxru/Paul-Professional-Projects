# INC-006 — Thread-pool starvation from blocking work (Broken)

- Started (UTC): 2026-09-02T22:34:07.6713758+00:00
- Requested operations: 20
- Elapsed: 319.88 ms
- Completed within budget: True

## Metrics

- **queueDelayP50Milliseconds:** 86.79
- **queueDelayP95Milliseconds:** 196.20
- **queueDelayP99Milliseconds:** 268.43
- **workerModel:** ThreadPool + blocking capacity gate
- **peakBusyWorkerThreads:** 9
- **boundedConcurrency:** 2

## Evidence

- Queue delay is measured from enqueue timestamp to the moment a work item starts consuming a worker slot.
- Fixed mode runs blocking work behind a bounded channel on dedicated long-running workers, preventing request workers from being held.

## Limitations

- The capacity gate makes the starvation signal repeatable without changing process-wide ThreadPool limits.
