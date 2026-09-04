# Case study — pathological vs optimised order/catalogue API

> **These numbers are synthetic benchmarks measured on a single developer machine over the loopback
> network. They are useful for reasoning about the *shape* of the improvement — not as absolute
> throughput or latency figures for any real production system.** See the caveats section.

## Machine and environment
| Property | Value |
|---|---|
| OS | Microsoft Windows 11 (build reported by `RuntimeInformation`) |
| CPU | 16 logical cores |
| RAM | 64 GB |
| Runtime | .NET SDK 10.0.400, target `net10.0` |
| Storage for SampleApi | SQLite file on the same host (`sampleapi.db`) |
| Network | loopback (`127.0.0.1:5027`) — client and server on the same host |
| SampleApi | `dotnet SampleApi.dll` in Release mode, `ASPNETCORE_ENVIRONMENT=Production` |
| Load runner | `loadrun` (this toolkit) in Release mode, no external tools |
| Seed | 200 products, 500 orders (deterministic seed) |

## Scenario
Steady state offered load: `40 requests/s` (open, constant arrival rate),
`3 s` warm-up excluded from the reported statistics,
`30 s` measurement window,
3 endpoints in the mix:

1. `GET /api/v1/orders?page=1&pageSize=25`
2. `GET /api/v1/catalog/products?category=coffee&page=1&pageSize=25`
3. `GET /api/v1/catalog/products/SKU-00042`

Baseline scenario applies pathology headers to force N+1 for the order-list endpoint and
`ToLower()`-in-predicate for both catalogue endpoints (defeats the SKU/category index), plus a
20 ms artificial downstream delay on the order-list and product-lookup steps.
Optimised scenario passes `X-Pathology-Optimised: true` on every step, which takes the
straight EF Core query paths with no artificial delay.

Total offered arrivals: `40 rps × 3 steps × 33 s ≈ 3 300`. Each candidate saw ~3 260 completed
requests over the full window (warm-up included) and the reported statistics come from the steady
30 s window only.

## Exact commands
```powershell
# 1. Start the sample API
$env:ASPNETCORE_URLS  = 'http://127.0.0.1:5027'
$env:ASPNETCORE_ENVIRONMENT = 'Production'
dotnet .\src\SampleApi\bin\Release\net10.0\SampleApi.dll

# 2. Reset pathology switches, then run the pathological baseline
Invoke-WebRequest -Method Delete -Uri http://127.0.0.1:5027/admin/pathology | Out-Null
dotnet .\src\LoadRunner.Cli\bin\Release\net10.0\loadrun.dll run `
    .\scenarios\case-study-baseline.json --results results --out results

# 3. Reset and run the optimised candidate
Invoke-WebRequest -Method Delete -Uri http://127.0.0.1:5027/admin/pathology | Out-Null
dotnet .\src\LoadRunner.Cli\bin\Release\net10.0\loadrun.dll run `
    .\scenarios\case-study-optimised.json --results results --out results

# 4. Compare with statistical significance test
dotnet .\src\LoadRunner.Cli\bin\Release\net10.0\loadrun.dll compare `
    .\results\case-study-baseline-*.json `
    .\results\case-study-optimised-*.json --out results
```

## Measured results

### Baseline — every pathology on
- Total requests: `3,289`
- Errors: `0` (`0.00%`)
- Throughput: `120.7 rps` (~40 rps × 3 steps)
- Service latency (measured request duration):
  - p50 = **33.7 ms**, p95 = **66.6 ms**, p99 = **97.4 ms**
- Intended-start latency (open-model wait + service, i.e. what a user would perceive):
  - p95 = **142 ms**, p99 = **218 ms**

### Optimised — pathologies bypassed
- Total requests: `3,261`
- Errors: `0` (`0.00%`)
- Throughput: `120.1 rps` (same offered load, same achieved rate)
- Service latency:
  - p50 = **6.7 ms**, p95 = **24.3 ms**, p99 = **38.5 ms**
- Intended-start latency:
  - p95 = **73 ms**, p99 = **105 ms**

### Comparison and significance
Deltas (candidate − baseline):

| Metric | Baseline | Candidate | Δ | % change |
|---|---:|---:|---:|---:|
| p50 (ms) | 33.75 | 6.68 | −27.06 | **−80.2 %** |
| p95 (ms) | 66.62 | 24.26 | −42.36 | **−63.6 %** |
| p99 (ms) | 97.43 | 38.46 | −58.96 | **−60.5 %** |
| intended p95 (ms) | 142.00 | 73.00 | −69.00 | **−48.6 %** |
| throughput (rps) | 120.73 | 120.09 | −0.64 | −0.5 % |
| error rate | 0.00 % | 0.00 % | 0.00 % | 0.0 % |

**Mann–Whitney U (two-sided, tie-corrected normal approximation)** on the per-second p95 samples:
- U1 = 811.00, U2 = 1.00
- z-score = −6.457
- **p-value < 0.0001**
- Verdict: **Improved**

**Bootstrap 95 % CI on the difference of medians** (1500 resamples, seed = 42):
- median baseline = 61.86 ms
- median candidate = 22.53 ms
- median Δ = −39.32 ms
- 95 % CI = [−45.20, −34.41] ms (entirely below zero)
- Verdict: **Improved**

**Overall verdict: Improved — both tests agree.** The candidate is faster at every latency
percentile reported by the toolkit, the confidence interval on the improvement is bounded well
away from zero, and throughput and error rate are unchanged. Under the arrival rate we drove,
the queueing amplification that would push the intended-p95 well above the service-p95 is real
but modest (the machine has ample capacity for 40 rps) — the fact that intended-p95 dropped from
142 ms to 73 ms is the honest measure of what a user would perceive.

## What actually changed
Three separate root causes were fixed in the "optimised" path:

1. **N+1 query on `GET /api/v1/orders`.** The pathological path fetches the page of orders and
   then issues `COUNT(*)` on `OrderLines` per order; the optimised path projects `o.Lines.Count`
   directly which EF Core translates into a single correlated subquery per row.
2. **Missing-index / non-sargable predicate on catalogue search and SKU lookup.** The
   pathological path wraps the column in `ToLower()`, which SQLite (and every other engine) cannot
   satisfy from an index on `Sku` or `Category`. The optimised path uses direct equality on the
   indexed column.
3. **Artificial downstream latency.** The pathological path awaits a fake 20 ms downstream
   service on the order-list and SKU lookup endpoints. The optimised path skips it. This is
   a stand-in for the real-world situation where a slow downstream service is on the critical
   path of every request and you don't realise it until you measure it end-to-end.

Fixes 1 and 2 are the ones an engineer would apply to a real API. Fix 3 is the reminder that
"just make the database faster" is not always the answer — sometimes the real cost is in a
network call your team didn't own.

## Raw artefacts
The two run JSONs and the comparison markdown/html are in `results/`:

- `results/case-study-baseline-20260902-224428-6116ae.json`
- `results/case-study-optimised-20260902-224506-ecc079.json`
- `results/compare-case-study-baseline-20260902-224428-6116ae-vs-case-study-optimised-20260902-224506-ecc079.md`
- `results/compare-case-study-baseline-20260902-224428-6116ae-vs-case-study-optimised-20260902-224506-ecc079.html`

Each JSON contains the full scenario definition, environment snapshot (OS, cores, runtime,
machine name), aggregate percentiles, per-endpoint percentiles, per-second time series, assertion
results, and any drift/knee/capacity records — everything you need to reproduce the analysis
without re-running the load.

## Caveats — please read before quoting these numbers
- **Loopback network.** Client and server share the same NIC (well, the same kernel loopback),
  so there is no realistic network cost in these figures. Real APIs pay 100 µs–1 ms per hop.
- **Same-host client/server contention.** The load runner and the API compete for CPU. When
  throughput matters (spike, stress, capacity search), some of what looks like server latency is
  actually cross-process context switching on this box.
- **SQLite, not a real database.** SQLite is a serialised, single-writer engine backed by a local
  file. Real deployments use PostgreSQL / MySQL / SQL Server with different lock behaviour,
  different plan optimisers, and different index cost models. The *shape* of the pathologies
  (N+1, non-sargable predicate, slow downstream) is transferable; the *magnitude* is not.
- **Load level intentionally low.** We drove 40 rps to keep the run < 30 s so the case study is
  reproducible in a documented commit. A capacity search would tell you the real ceiling on this
  box, but that is a different question from "did the optimisation help".
- **A single developer machine, run once each.** These numbers are not averaged across runs. The
  Mann–Whitney U test is against the *within-run* per-second p95 samples, not across repeated
  runs. If you need publication-quality numbers, repeat the run 10× and use `loadrun compare`
  to check the run-to-run variability first.
- **Warm-up.** The first 3 seconds of both runs were excluded from statistics — SQLite page
  cache warm-up and JIT compilation account for most of that.

## Reproducibility
The scenarios that produced these numbers are checked in verbatim
(`scenarios/case-study-baseline.json`, `scenarios/case-study-optimised.json`).
The seeded database is deterministic (`Seed.EnsureAsync(db, productCount: 200, orderCount: 500)`
uses a fixed RNG seed). Running the four commands above on a similar 16-core machine should
land inside the same order of magnitude; on a much smaller machine, the pathological p95 will
be worse and the optimised p95 will be similar — which is the useful shape.
