# LoadRunner report — case-study-optimised

- Run id: `case-study-optimised-20260902-224506-ecc079`
- Started (UTC): 2026-09-02 22:45:06
- Duration: 30.2s
- Warmup skipped: 00:00:03 (339 samples)
- Runtime: .NET 10.0.11
- Host: CPC-rukwa-0IU7J (Microsoft Windows 10.0.26200, 16 cores)

## Aggregate

| Metric | Value |
|---|---|
| Count | 3,261 |
| Throughput | 120.1 rps |
| Errors | 0 (0.00%) |
| Latency p50 / p95 / p99 | 6.7 / 24.3 / 38.5 ms |
| Latency p99.9 / max | 56.2 / 78.7 ms |
| Intended p95 / p99 / max | 73.0 / 105.0 / 144.0 ms |
| Errors — conn / timeout / 4xx / 5xx / assertion | 0 / 0 / 0 / 0 / 0 |

## Per-step results

### ListOrders

| Metric | Value |
|---|---|
| Count | 1,087 |
| Throughput | 40.1 rps |
| Errors | 0 (0.00%) |
| Latency p50 / p95 / p99 | 7.3 / 26.8 / 43.1 ms |
| Latency p99.9 / max | 58.5 / 78.7 ms |
| Intended p95 / p99 / max | 54.0 / 80.0 / 114.0 ms |
| Errors — conn / timeout / 4xx / 5xx / assertion | 0 / 0 / 0 / 0 / 0 |

### SearchCatalog

| Metric | Value |
|---|---|
| Count | 1,087 |
| Throughput | 40.0 rps |
| Errors | 0 (0.00%) |
| Latency p50 / p95 / p99 | 7.1 / 26.1 / 38.4 ms |
| Latency p99.9 / max | 48.4 / 52.3 ms |
| Intended p95 / p99 / max | 71.0 / 99.0 / 137.0 ms |
| Errors — conn / timeout / 4xx / 5xx / assertion | 0 / 0 / 0 / 0 / 0 |

### GetProduct

| Metric | Value |
|---|---|
| Count | 1,087 |
| Throughput | 40.1 rps |
| Errors | 0 (0.00%) |
| Latency p50 / p95 / p99 | 5.4 / 21.5 / 30.8 ms |
| Latency p99.9 / max | 43.0 / 48.7 ms |
| Intended p95 / p99 / max | 85.0 / 116.0 / 144.0 ms |
| Errors — conn / timeout / 4xx / 5xx / assertion | 0 / 0 / 0 / 0 / 0 |

## Assertions
| Metric | Op | Target | Actual | Result |
|---|---|---|---|---|
| `latency.p95` | `<` | 250.000 | 24.262 | **PASS** |
| `error_rate` | `<` | 0.010 | 0.000 | **PASS** |
