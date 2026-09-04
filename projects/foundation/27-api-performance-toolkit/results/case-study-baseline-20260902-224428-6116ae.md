# LoadRunner report — case-study-baseline

- Run id: `case-study-baseline-20260902-224428-6116ae`
- Started (UTC): 2026-09-02 22:44:28
- Duration: 30.1s
- Warmup skipped: 00:00:03 (311 samples)
- Runtime: .NET 10.0.11
- Host: CPC-rukwa-0IU7J (Microsoft Windows 10.0.26200, 16 cores)

## Aggregate

| Metric | Value |
|---|---|
| Count | 3,289 |
| Throughput | 120.7 rps |
| Errors | 0 (0.00%) |
| Latency p50 / p95 / p99 | 33.7 / 66.6 / 97.4 ms |
| Latency p99.9 / max | 162.7 / 238.3 ms |
| Intended p95 / p99 / max | 142.0 / 218.0 / 1680.0 ms |
| Errors — conn / timeout / 4xx / 5xx / assertion | 0 / 0 / 0 / 0 / 0 |

## Per-step results

### ListOrders

| Metric | Value |
|---|---|
| Count | 1,088 |
| Throughput | 40.0 rps |
| Errors | 0 (0.00%) |
| Latency p50 / p95 / p99 | 42.7 / 78.5 / 115.7 ms |
| Latency p99.9 / max | 168.9 / 238.3 ms |
| Intended p95 / p99 / max | 98.0 / 137.0 / 238.0 ms |
| Errors — conn / timeout / 4xx / 5xx / assertion | 0 / 0 / 0 / 0 / 0 |

### GetProduct

| Metric | Value |
|---|---|
| Count | 1,113 |
| Throughput | 40.9 rps |
| Errors | 0 (0.00%) |
| Latency p50 / p95 / p99 | 36.0 / 65.8 / 109.8 ms |
| Latency p99.9 / max | 162.7 / 168.8 ms |
| Intended p95 / p99 / max | 180.0 / 521.0 / 1680.0 ms |
| Errors — conn / timeout / 4xx / 5xx / assertion | 0 / 0 / 0 / 0 / 0 |

### SearchCatalog

| Metric | Value |
|---|---|
| Count | 1,088 |
| Throughput | 40.2 rps |
| Errors | 0 (0.00%) |
| Latency p50 / p95 / p99 | 6.3 / 24.1 / 37.1 ms |
| Latency p99.9 / max | 51.0 / 110.4 ms |
| Intended p95 / p99 / max | 114.0 / 148.0 / 241.0 ms |
| Errors — conn / timeout / 4xx / 5xx / assertion | 0 / 0 / 0 / 0 / 0 |

## Assertions
| Metric | Op | Target | Actual | Result |
|---|---|---|---|---|
| `latency.p95` | `<` | 5000.000 | 66.623 | **PASS** |
| `error_rate` | `<` | 0.200 | 0.000 | **PASS** |
