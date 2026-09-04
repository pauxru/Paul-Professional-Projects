# Methodology — how to run a defensible load test

A load test that isn't defensible isn't worth the CI minutes it burns. This document is the
short version of the reasoning behind the toolkit's design and how to use it responsibly.

## The four questions that make a report defensible

Before writing anything down as an answer, be able to answer:

1. **What was the offered load?** Open-model at 40 rps is different from closed-model with
   40 VUs. State it precisely.
2. **What is the measurement window?** Warm-up excluded, steady-state duration, clock source.
3. **How many samples are behind each percentile?** A p99 from 100 samples is a single
   sample; treat it as such.
4. **How does the result compare to a baseline, statistically?** "5 % slower" without a
   significance test is a coin flip.

## Warm-up

The first 3–10 seconds of any run pay for JIT compilation, database connection pool warm-up,
and OS page cache priming. The toolkit's `warmUp` field excludes samples in that window from
the reported statistics. **Never report a run that included the cold start.**

Pick warm-up empirically: run a slightly longer run once, look at the time-series RPS chart,
find where it flattens.

## Steady state

Run long enough that the reported percentile is stable. Rule of thumb:

- p50 stable within ~2 min for a typical API.
- p95 stable within ~2 min at ≥ 20 rps (≥ 2400 samples).
- p99 needs at least ~10 000 samples for the number to mean anything, i.e. a couple of minutes
  at 100 rps or ten minutes at 20 rps.
- p99.9 needs an order of magnitude more; if you're gating CI on p99.9 you probably shouldn't.

## Sample size guidance

The rank of the k-th percentile in a sorted sample of size `n` is `ceil(k * (n+1)/100)`. So:

| Percentile | Minimum samples to have any hope of stability |
|---|---|
| p50 | 100 |
| p90 | 500 |
| p95 | 1 000 |
| p99 | 10 000 |
| p99.9 | 100 000 |

Below the minimum, the toolkit still reports the number, but the report card labels it as
"low-N" and refuses to gate on it in CI.

## Environment control

The single biggest source of noise is the environment.

- Same machine for baseline and candidate. Same load (no compiling in the background).
- Same OS-level settings: CPU governor, network buffers, disk cache.
- Ideally: an isolated CI runner, not a shared build agent. If you must share, document it.
- Warm the JIT + DB pool + downstream *before* every run, including the baseline.

If you have to compare across machines, take *many* runs on each, report the run-to-run
variance, and use the toolkit's `compare` command over medians of multiple runs.

## Why averages lie

Latency distributions are heavily right-skewed. A 10 ms mean with a 200 ms p99 is entirely
normal — the average is dominated by the mass at the p50 while the tail is where users
suffer. Report percentiles, not averages. And in particular: **for user-facing APIs, report
intended-start p95 (which includes queueing)** because that is what a user would actually
perceive.

## Open vs closed model — which do I want?

Ask what generates arrivals in production:

- A pool of user sessions clicking through a web app → *closed* — the fixed number of
  concurrent sessions is the natural model.
- A public API called by external clients → *open* — external clients don't wait for you
  to finish before starting a new request.
- A queue-based background worker → *closed* (bounded by pool size).
- A high-fanout webhook receiver → *open*.

Default to open when in doubt for CI gating because it catches overload amplification.

## Coordinated omission — the fix

If you use the closed model, every VU is "coordinated" with the server: it waits for a slow
response before starting the next request, so long tails get under-sampled. The toolkit's
open model schedules arrivals at wall-clock intervals and records both the service latency
(server-side duration) and the intended-start latency (from when the request should have
started, including queueing). Reports show both.

Gate on intended-start p95 for user-perceived latency; on service p95 for server-side SLO.

## Statistical comparison

The `compare` command runs both Mann–Whitney U (rank test, non-parametric) and a bootstrap
95 % confidence interval on the median difference. A regression that passes both tests is
a real regression; a difference that fails both is noise. See ADR-005 for the reasoning.

## What invalidates a run

Refuse to report a run if any of these are true:

- The load generator was CPU-saturated (client-side, not server-side). Check by running the
  same scenario at half the arrival rate — if service p95 halves, you were saturating the
  client.
- The run failed to reach steady state (`throughputRps` still climbing at the end of the
  window).
- More than 1 % of requests error out on transport-level failures (`Connection`, `Timeout`).
  Those aren't latency data; they're a broken run.
- The scenario ran against a shared environment during someone else's test.

The toolkit will still produce a report — it's your job to decide it's not the run to quote.

## Tunable pathologies

The sample API in `src/SampleApi` ships with six deliberately-broken modes, switchable
per-request via `X-Pathology-*` headers or globally via `POST /admin/pathology`:

| Header | Effect |
|---|---|
| `X-Pathology-NPlusOne: true` | `GET /api/v1/orders` issues COUNT(*) per row instead of a single query with projection. |
| `X-Pathology-MissingIndex: true` | Catalogue lookups wrap the column in `ToLower()`, defeating the SKU/category index. |
| `X-Pathology-DownstreamLatencyMs: 20` | Adds 20 ms `Task.Delay` on the request path, standing in for a slow downstream call. |
| `X-Pathology-LockContention: true` | `POST /api/v1/orders` acquires a shared `SemaphoreSlim(1,1)` around the write path. |
| `X-Pathology-MemoryLeak: true` | Each `POST /api/v1/orders` retains a 64 KB buffer in a static list; recover with the pathology reset. |
| `X-Pathology-Optimised: true` | Take the good path for every endpoint regardless of the other switches. |

The case study (`docs/case-study-optimisation.md`) uses these to demonstrate a 3–5×
latency improvement with statistical significance.
