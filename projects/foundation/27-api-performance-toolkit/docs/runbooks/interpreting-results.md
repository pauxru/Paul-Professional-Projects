# Runbook — interpreting results

The CLI prints a summary. The HTML report shows the same data with charts. The JSON
contains everything, including the per-second time series and per-endpoint breakdown.

## What each field means

| Field | Meaning |
|---|---|
| `Aggregate.Count` | Total requests across all endpoints in the steady-state window. |
| `Aggregate.Errors` | Requests with `ErrorKind != None`. |
| `Aggregate.ErrorRate` | `Errors / Count`. |
| `Aggregate.ThroughputRps` | `Count / steadyStateDuration`. |
| `Service.P50/P95/P99` | Percentiles of measured request duration in milliseconds. |
| `Intended.P50/P95/P99` | Percentiles of `completedTime - intendedStartTime` — includes queueing. Open model only; closed-model shows these equal to service. |
| `PerStep` | Same block, per endpoint. |
| `TimeSeries` | Per-second RPS, error rate, service p95, intended p95. |
| `Assertions` | Each threshold with `Passed=true/false`, `Actual`, and the operator. |
| `Warmup` | The excluded window. |
| `Environment` | OS, cores, .NET runtime, machine, and optional git commit. |
| `Knee` | For stress runs — the arrival rate at which p95 or error-rate crossed the threshold. |
| `Capacity` | For capacity searches — the max sustained rate under the p95 target. |
| `SoakDrift` | For soak runs — `LatencySlopeMsPerMinute` and `ThroughputSlopeRpsPerMinute` from linear regression on the time series. |

## Reading the summary — a worked example

```
Total requests: 3,289  Errors: 0 (0.00%)
Throughput: 120.7 rps
Service latency  p50/p95/p99: 33.7 / 66.6 / 97.4 ms
Intended latency p95/p99:      142.0 / 218.0 ms
Assertions:
  [PASS] latency.p95 < 5000.000 => 66.623
  [PASS] error_rate < 0.200 => 0.000
```

- **3 289 requests over ~30 s** — that's ~110 rps aggregate, and we asked for 40 rps × 3
  steps = 120 rps offered. The 3-second warm-up eats into the count. Good.
- **0 errors** — clean run.
- **Service p95 = 66.6 ms** — this is what the server took, on its side.
- **Intended p95 = 142 ms** — this is what a user would perceive. The delta is the
  coordinated-omission story: 76 ms of queueing was hidden inside the fast responses.
- **Both assertions passed** — exit code 0.

## When intended latency ≫ service latency

That means the system was queueing behind requests. Under an open-model run, this is the
signal that offered load ≥ system capacity — even if the server never emits a 5xx. If your
users care about response time (they do), gate on intended p95.

## When intended latency ≈ service latency

Either the run was closed model (by design), or the open-model system had ample headroom.
Both are fine — check the report banner to see which model was used.

## When throughput ≪ offered rate

- Open model: workers can't dispatch fast enough → check `MaxVUs`.
- Closed model: this is normal; throughput is emergent.

## When errors are up

Look at the per-endpoint block for the endpoint doing the most errors, and check the error
kind:

- `Connection` — the server closed the socket or refused it. Usually a socket exhaustion
  or IIS / Kestrel connection limit.
- `Timeout` — the request exceeded the 30 s deadline. The server is very slow, not down.
- `Http4xx` — client-side (auth, validation, throttling). Not a server problem; verify
  the scenario.
- `Http5xx` — server-side. This is what you're looking for.
- `AssertionFailure` — the response's status didn't match `ExpectedStatus`. Check the
  scenario.

## Reading the time series

The JSON `TimeSeries` is one point per second. Plot `Rps`, `ErrorRate`, `P95Ms`, and
`IntendedP95Ms`. Signs to look for:

- **Rps flat at target** → offered load held. Good.
- **Rps declining across a stress ramp** → server is slowing down; the open-model scheduler
  is filling the channel and worker pool is saturated. `MaxVUs` may be too low, OR the
  server is genuinely overloaded (which is the point of stress).
- **P95 climbing while Rps is flat** → warm-up not yet done, or a resource (GC pause, DB
  page cache, connection pool warming). Extend the warm-up.
- **IntendedP95 flat but P95 climbing** → sub-second queueing; you're near the knee.

## The comparison report

`loadrun compare baseline candidate` prints a Markdown table with deltas, then two
significance sections:

- **Mann–Whitney U** — rank test. `p-value < 0.05` and the sign of the z-score tells
  you whether candidate is faster or slower than baseline.
- **Bootstrap CI on median difference** — 95 % CI on `median(candidate) - median(baseline)`.
  If the entire CI is < 0 → candidate is faster; > 0 → candidate is slower; if it
  straddles 0 → no significant change.

Overall verdict is `Improved / Regressed / NoSignificantChange`. Exit code 1 on `Regressed`.

**When the tests disagree.** The report takes Mann–Whitney U as primary, unless Mann–Whitney
says NoSignificantChange and bootstrap disagrees — in which case the bootstrap's effect-size
answer wins (the U test is under-powered on small samples).

## Related runbooks

- [`running-a-load-test.md`](running-a-load-test.md)
