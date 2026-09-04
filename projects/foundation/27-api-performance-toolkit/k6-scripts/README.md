# k6 scripts — NOT EXECUTED on this build host

> **The k6 binary is not installed on the build host that produced this repository.
> These scripts are shipped as portable artefacts for teams that already use k6.
> The `loadrun` CLI in this repo is the reference implementation that was actually run.**

Each `.js` file in this folder corresponds to a scenario JSON in `../scenarios/` and is
intended to produce the same offered load. The two implementations should produce
comparable service-latency percentiles for the same target; run them both and check.

## Scripts

| File | Corresponds to | Purpose |
|---|---|---|
| `smoke.js` | `../scenarios/smoke.json` | Two VUs, five seconds. Sanity check. |
| `load.js` | `../scenarios/load.json` | Ramping VUs. |
| `stress.js` | `../scenarios/stress.json` | Ramping arrival rate to a knee. |
| `spike.js` | `../scenarios/spike.json` | Base + peak + return. |
| `soak.js` | `../scenarios/soak.json` | Steady rate for a long time. |
| `case-study-baseline.js` | `../scenarios/case-study-baseline.json` | The pathological scenario. |
| `case-study-optimised.js` | `../scenarios/case-study-optimised.json` | The optimised scenario. |

## How to run (on a machine that HAS k6)

```bash
# assumes SampleApi is running on http://127.0.0.1:5027
k6 run k6-scripts/smoke.js
k6 run --summary-trend-stats='p(50),p(95),p(99)' k6-scripts/case-study-baseline.js
```

## Why they're here

Some teams already have k6 in CI and want a second opinion on the numbers. The
`loadrun compare` command works only on `loadrun`'s own JSON output — but if you can
produce a k6 summary JSON and a `loadrun` summary JSON for the same scenario, the
Mann–Whitney U test in this repo will still run over the raw per-second time series if
you post-process the k6 output into the same shape.

## Divergences from the `loadrun` behaviour

- k6's `constant-arrival-rate` executor with `maxVUs` is the same open model, but the
  implementation details of the scheduler are k6-side.
- k6 does not, by default, report an "intended-start latency" — its `http_req_duration`
  is service latency only. If you want the coordinated-omission story here you'd need
  a custom `Trend` and to record the intended start yourself in the scenario.
