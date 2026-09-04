# INC-004 — Static memory retention leak (Fixed)

- Started (UTC): 2026-09-02T22:34:47.1324031+00:00
- Requested operations: 20
- Elapsed: 2.47 ms
- Completed within budget: True

## Metrics

- **retainedPayloads:** 0
- **heapDeltaBytesAfterForcedGc:** 25464
- **allocatedBytes:** 649264
- **payloadBytesPerRequest:** 32768
- **staticHandlerSubscriptions:** 0

## Evidence

- GC.GetTotalMemory(true) delta: 25464 bytes.
- GC.GetTotalAllocatedBytes delta: 649264 bytes.

## Limitations

- The retained-object count is the primary deterministic signal; GC heap deltas can vary across runtime versions.
