# Portfolio summary

**Project:** API Performance & Load Testing Toolkit — `loadrun`

**Positioning:** self-directed engineering case study. This is the kind of tool an
engineer brings when the brief is *"our API is slow and we don't know why"* — the
combination of a rigorous load runner and a defensible statistical comparison.

## What it is

A .NET 10 CLI plus reusable core libraries that drive HTTP load, capture per-request samples,
compute HdrHistogram-style latency statistics, and produce self-contained HTML / Markdown /
JSON reports. Runs entirely offline. Ships with a bundled sample API that has six tunable
pathologies so the toolkit has something real to find.

## What is engineeringly interesting about it

1. **Open- and closed-model load with coordinated-omission handling.** Both service latency
   and intended-start latency are recorded so slow responses can't hide queueing. Most
   homegrown load tools quietly ignore this and their numbers systematically over-state
   how fast the system is under overload.
2. **Statistical significance for comparison.** Mann–Whitney U (rank-based, tie-corrected)
   plus a bootstrap 95 % confidence interval on the median difference. The `compare` command
   returns Improved / Regressed / NoSignificantChange with p-values and CI bounds, not just
   "5 % slower".
3. **HdrHistogram-style bucket layout with a unit-tested precision bound.** Bounded memory,
   sub-microsecond record cost, composable across streams. Tested to within its stated
   relative-error bound against a known distribution.
4. **A real measured before/after case study.** Not a fabricated table: the numbers in
   `docs/case-study-optimisation.md` were produced by actually running the toolkit against
   the sample API in Release mode and are backed by raw result JSON in `results/`.

## What it is deliberately not

- Not a security scanner.
- Not a compliance artefact.
- Not a distributed load generator.
- Not tested at production scale (this is a single-developer-host build).

## Numbers a reviewer can quote

From the real case study:

- Baseline (all pathologies on) service p95 = **66.6 ms**, intended p95 = **142 ms**.
- Optimised service p95 = **24.3 ms**, intended p95 = **73 ms**.
- Latency improvement: **−63 % service p95, −49 % intended p95**.
- Mann–Whitney U p-value **< 0.0001**; bootstrap 95 % CI on median Δ = `[−45.2, −34.4] ms`.
- Verdict: **Improved** — both tests agree.

## How to demo in 60 seconds

See [`demo-script.md`](demo-script.md).

## Where it sits on a CV

Bullet: *Built a reusable API performance & load-testing toolkit in .NET 10 with
correct open/closed load models, HdrHistogram-style latency statistics with
coordinated-omission handling, and Mann–Whitney U + bootstrap significance testing
for CI-gating regression comparison. Verified with 72 passing tests and a real measured
before/after case study.*
