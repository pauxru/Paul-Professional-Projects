# INC-008 — Cascading retry storm (Broken)

- Started (UTC): 2026-09-02T22:34:18.9344456+00:00
- Requested operations: 12
- Elapsed: 8.70 ms
- Completed within budget: True

## Metrics

- **actualDownstreamCalls:** 324
- **callsPerLogicalRequest:** 27.00
- **amplificationVersusOneAttempt:** 27.00
- **circuitRejectedRequests:** 0
- **policy:** three attempts at gateway, service, and repository layers

## Evidence

- OutboundCallCounter observed 324 actual failing downstream calls.
- Broken composition is 3 × 3 × 3 attempts per logical request when the dependency always fails.

## Limitations

- The circuit breaker is intentionally small and in-process for reproducibility; distributed breakers need shared state and careful partitioning.
