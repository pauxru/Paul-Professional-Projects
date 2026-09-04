# Runbook — running a load test

Audience: an engineer who has never used this toolkit before.

## Before you start — authorisation

1. Do you own the target system? If not, do you have written permission?
2. Is the target environment separate from production?
3. Is anyone else running load right now? (Check the SRE channel.)
4. Do you know the kill switch? (`Ctrl-C` on the CLI; run persists what it has.)

If any of those are "no", stop.

## Prerequisites

- .NET SDK 10.0.400 (or a compatible 10.x SDK).
- The scenario JSON in `scenarios/` you want to run.
- If the target is remote: network connectivity and any required auth token
  (as `$env:LOADRUN_TOKEN` or similar; the scenario template picks it up via
  `{{env.LOADRUN_TOKEN}}`).

## Steps — running against the bundled SampleApi

```powershell
# 1. Build once
Set-Location C:\path\to\27-api-performance-toolkit
dotnet build -c Release

# 2. Start SampleApi in one shell
$env:ASPNETCORE_URLS='http://127.0.0.1:5027'
$env:ASPNETCORE_ENVIRONMENT='Production'
dotnet .\src\SampleApi\bin\Release\net10.0\SampleApi.dll

# 3. In a second shell, run a scenario
dotnet .\src\LoadRunner.Cli\bin\Release\net10.0\loadrun.dll `
    run .\scenarios\smoke.json --results results --out results
```

Look at the printed summary. If assertions pass, exit code is 0; otherwise 1.

## Steps — running against your API

```powershell
# 1. Copy a scenario and edit the baseUrl and steps
Copy-Item scenarios\smoke.json scenarios\my-api-smoke.json
notepad scenarios\my-api-smoke.json    # set baseUrl, adjust steps

# 2. If the API needs auth, set the env var and use templating
$env:LOADRUN_TOKEN = "<the token>"
# In the scenario, headers: { "Authorization": "Bearer {{env.LOADRUN_TOKEN}}" }

# 3. Run
dotnet .\src\LoadRunner.Cli\bin\Release\net10.0\loadrun.dll `
    run .\scenarios\my-api-smoke.json
```

## The six load models — when to pick each

- **`ConstantVUs`** (closed): pool of concurrent users, each running back-to-back. Use for
  bounded-concurrency systems (worker pools, service buses).
- **`RampingVUs`** (closed): step function of concurrency over time. Use to profile the
  system at several concurrency levels in a single run.
- **`ConstantArrivalRate`** (open): steady offered rate regardless of server speed. Use
  for user-facing APIs and CI regression gating.
- **`RampingArrivalRate`** (open): step function of arrival rate. Use to find the shape
  of the latency curve as offered load grows.
- **`Stress`**: automatic ramp until knee (p95 or error-rate threshold breached). Use for
  breaking-point analysis. Not for CI gating.
- **`Spike`**: base load then a peak then back — measures recovery. Use to check autoscaling
  and connection-pool warmup.
- **`Soak`**: steady load for a long time (minutes to hours) with linear-regression drift
  detection. Use to catch memory leaks and connection exhaustion.
- **`CapacitySearch`**: binary search for the maximum arrival rate that keeps p95 under a
  target. Use for sizing.

## Interpreting the numbers

Read the summary top-to-bottom:

- `Total requests` and `Errors` — did the run happen at all?
- `Throughput` — did we actually offer the load we intended?
- `Service latency` p50/p95/p99 — server-side.
- `Intended latency` p95/p99 — user-perceived (open model only).
- `Assertions` — pass/fail for anything you gated on.

See [`interpreting-results.md`](interpreting-results.md) for the deeper read.

## Persisting and comparing runs

Every run is saved to `results/<runId>.json`. To compare two runs:

```powershell
dotnet .\src\LoadRunner.Cli\bin\Release\net10.0\loadrun.dll `
    compare .\results\case-study-baseline-*.json .\results\case-study-optimised-*.json `
    --out results
```

The comparison produces a Markdown + HTML report with the statistical verdict. Exit code
is 1 if the candidate has regressed (Mann–Whitney U + bootstrap CI both agree, or
Mann–Whitney U alone with rank-based confidence).

## Kill switch

`Ctrl-C` on the CLI is graceful — the collector snapshots what it has and writes the
report. If you have a runaway VU pool, `Stop-Process -Id <PID>` on the CLI PID is safe:
the target API is unaffected.

## If the run fails

- **Assertion failed**: read the report — it names the metric, threshold, and actual.
- **Scenario not found**: exit code 2. Check the path is relative to your cwd.
- **All requests timed out**: the target is down or the URL is wrong.
- **`Unauthorized` on every request**: the token expired or wasn't picked up. Check the
  templating; the executor logs the resolved URL but not the header.

## Related runbooks

- [`interpreting-results.md`](interpreting-results.md) — how to read the report.
