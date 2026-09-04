# INC-008 — Cascading retry storm (Fixed)

- Started (UTC): 2026-09-02T22:35:17.8502057+00:00
- Requested operations: 12
- Elapsed: 29.84 ms
- Completed within budget: True

## Metrics

- **actualDownstreamCalls:** 2
- **callsPerLogicalRequest:** 0.17
- **amplificationVersusOneAttempt:** 0.17
- **circuitRejectedRequests:** 11
- **policy:** two-call global retry budget, circuit breaker threshold 2, deterministic jitter

## Evidence

- OutboundCallCounter observed 2 actual failing downstream calls.
- Broken composition is 3 × 3 × 3 attempts per logical request when the dependency always fails.

## Limitations

- The circuit breaker is intentionally small and in-process for reproducibility; distributed breakers need shared state and careful partitioning.
