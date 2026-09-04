# INC-007 — Downstream timeout and request pile-up (Fixed)

- Started (UTC): 2026-09-02T22:35:13.4165717+00:00
- Requested operations: 20
- Elapsed: 381.65 ms
- Completed within budget: True

## Metrics

- **completedResponses:** 0
- **timeoutResponses:** 20
- **p50LatencyMilliseconds:** 31.83
- **p95LatencyMilliseconds:** 35.76
- **peakDependencyPileUp:** 3
- **configuredClientTimeout:** 20 ms
- **simulatedDependencyDelayMilliseconds:** 70

## Evidence

- The in-process HttpMessageHandler observes cancellation tokens from HttpClient.
- Peak concurrent downstream calls was 3.

## Limitations

- This models a slow dependency without using a network service; socket, DNS, and proxy failure signatures require a production-like environment.
