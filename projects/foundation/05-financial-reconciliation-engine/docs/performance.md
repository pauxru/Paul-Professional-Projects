# Performance

> **These numbers are real** — they are produced by `ReconEngine.PerfHarness`, which measures the
> engine on this host at run time. Re-run the command below to reproduce them; they are not hand-edited.

## Host

- Logical cores: `16`
- OS: `Microsoft Windows 10.0.26200`
- Runtime: `.NET 10.0.11`
- Architecture: `X64` / process `X64`
- Measured at: `2026-09-02 23:24:50 UTC`

## Command

```powershell
dotnet run -c Release --project src/ReconEngine.PerfHarness -- --sizes 10000,100000,250000
```

## Reconcile hot path (dedup + 7-rule pipeline + classification + balance)

`pairs` is the number of internal transactions; `rows` counts both internal and external records fed to the engine.

| pairs | rows | wall time (ms) | throughput (rows/s) | peak working set (MB) | alloc (MB) | matches | exceptions |
|------:|-----:|---------------:|--------------------:|----------------------:|-----------:|--------:|-----------:|
| 10,000 | 20,000 | 93.5 | 213,827 | 68.6 | 18.8 | 10,000 | 0 |
| 100,000 | 200,000 | 867.8 | 230,463 | 278.3 | 179.1 | 100,000 | 0 |
| 250,000 | 500,000 | 2,165.5 | 230,897 | 545.5 | 419.3 | 250,000 | 0 |

## Ingestion (streaming CSV parse + normalise, no DB)

| rows | wall time (ms) | throughput (rows/s) |
|-----:|---------------:|--------------------:|
| 100,000 | 649.1 | 154,058 |

## Hot-path optimisation: naive scan vs indexed lookup (before / after)

Exact-reference matching over 20,000 clean pairs (40,000 rows). The naive matcher does an
O(n·m) nested scan; the shipped engine builds an O(1) dictionary index keyed by (currency, reference, amount).

| strategy | complexity | wall time (ms) | throughput (rows/s) | matches |
|----------|------------|---------------:|--------------------:|--------:|
| naive (before) | O(n·m) | 153.2 | 261,064 | 20,000 |
| indexed (after) | O(n) | 32.5 | 1,231,724 | 20,000 |

**Speed-up: 4.7×** on this host. The gap widens as row counts grow, which is why the shipped
pipeline never uses nested scans — every rule is backed by a dictionary or day-bucketed index.

## Method & honesty notes

- Throughput is wall-clock; a JIT warm-up run precedes the measured runs.
- Peak working set is `Process.PeakWorkingSet64` (process-wide, monotonic), so later rows report the high-water mark.
- Allocations are `GC.GetTotalAllocatedBytes` deltas around the measured call.
- The datasets are produced by the same `SyntheticDataGenerator` used by the tests, seeded for repeatability.
