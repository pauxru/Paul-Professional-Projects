# INC-007 — Downstream timeout and request pile-up (Broken)

- Started (UTC): 2026-09-02T22:34:14.2422748+00:00
- Requested operations: 20
- Elapsed: 434.50 ms
- Completed within budget: True

## Metrics

- **completedResponses:** 20
- **timeoutResponses:** 0
- **p50LatencyMilliseconds:** 78.43
- **p95LatencyMilliseconds:** 87.24
- **peakDependencyPileUp:** 6
- **configuredClientTimeout:** none (global scenario budget only)
- **simulatedDependencyDelayMilliseconds:** 70

## Evidence

- The in-process HttpMessageHandler observes cancellation tokens from HttpClient.
- Peak concurrent downstream calls was 6.

## Limitations

- This models a slow dependency without using a network service; socket, DNS, and proxy failure signatures require a production-like environment.
