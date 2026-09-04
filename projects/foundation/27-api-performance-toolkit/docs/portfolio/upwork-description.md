# Upwork portfolio description

**Title:** API performance & load-testing toolkit for .NET APIs

**One-liner:** A statistically defensible load runner and regression detector for HTTP APIs.

**Description:**

I built a reusable API performance and load-testing toolkit in .NET 10 to fill a gap I've
seen on repeated client engagements: homegrown load harnesses that produce plausible-looking
numbers that are actually unreliable. Three specific failures show up over and over — closed-
model tools that silently hide overload, naive "5 % slower means regression" comparisons
without a significance test, and coordinated omission that under-samples the slow tail.

The toolkit fixes all three by construction:

- **Load runner** built from the ground up with correct open (constant / ramping arrival
  rate) *and* closed (constant / ramping VUs) models, plus stress with automatic knee
  detection, spike with recovery measurement, soak with linear-regression drift detection,
  and a binary-search capacity finder for the maximum sustainable arrival rate under a
  p95 target.
- **Latency statistics** with an HdrHistogram-style bucket layout (bounded precision at
  any scale, unit-tested to its stated relative-error bound) plus exact retained-sample
  percentiles for small runs. Both service latency and intended-start latency are recorded
  — coordinated omission is surfaced, not hidden.
- **Regression comparison with statistical significance** — Mann–Whitney U (non-parametric,
  tie-corrected) plus a bootstrap 95 % confidence interval on the median difference. The
  `compare` command returns Improved / Regressed / NoSignificantChange with p-values and
  CI bounds, and its exit code gates a CI build.
- **Self-contained HTML reports** with hand-rolled SVG charts (no CDN, no CI-blocked JS),
  Markdown output for PR comments, JSON for machine consumption.

The repo ships a bundled sample API on SQLite with six deliberately-broken modes (N+1
query, missing-index / non-sargable predicate, downstream latency injection, lock
contention, memory leak, and an "optimised" reference) so the toolkit has something real
to find. A real measured before/after case study is included: `−63 %` service p95 and
`−49 %` intended p95, Mann–Whitney U p-value `< 0.0001`, bootstrap 95 % CI on the median
difference entirely below zero.

**Tech:** .NET 10 (`net10.0`), ASP.NET Core Minimal APIs, EF Core + SQLite,
`SocketsHttpHandler`, `System.Text.Json`, xUnit, `WebApplicationFactory`.

**Verification:** 72 tests pass in Release. Zero external infrastructure required to
build or test. Ships k6 scripts as portable artefacts for teams that already use k6
(the k6 binary was not installed on the build host and those scripts are marked as
NOT executed here).

**What I did:**

- Designed the engine, load models, scheduler, statistics, analysis, and reporting layers
  from scratch. No adapted third-party engine.
- Wrote the JSON scenario schema, validation, templating, CSV feeder, and correlation
  extraction.
- Implemented the histogram, precision-tested it against a known distribution.
- Implemented Mann–Whitney U (tie-corrected normal approximation) and the bootstrap CI,
  unit-tested against identical and shifted distributions to verify no false positives
  on same-distribution data.
- Built the sample API and the six switchable pathologies.
- Ran the toolkit against the sample API in Release to produce the case study — the
  numbers in the doc are the real measurements from that run.

**What it is deliberately not:**

- Not a security scanner or fuzz tester.
- Not a distributed load platform (single-node by design; extension noted as future work).
- Not audited by a third party.
- No production customer data anywhere; the sample API is fully synthetic.
