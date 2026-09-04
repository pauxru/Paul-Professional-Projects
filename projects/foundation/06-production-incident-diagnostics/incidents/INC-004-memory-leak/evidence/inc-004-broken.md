# INC-004 — Static memory retention leak (Broken)

- Started (UTC): 2026-09-02T22:33:55.3656239+00:00
- Requested operations: 20
- Elapsed: 2.63 ms
- Completed within budget: True

## Metrics

- **retainedPayloads:** 20
- **heapDeltaBytesAfterForcedGc:** 650672
- **allocatedBytes:** 653208
- **payloadBytesPerRequest:** 32768
- **staticHandlerSubscriptions:** 20

## Evidence

- GC.GetTotalMemory(true) delta: 650672 bytes.
- GC.GetTotalAllocatedBytes delta: 653208 bytes.

## Limitations

- The retained-object count is the primary deterministic signal; GC heap deltas can vary across runtime versions.
