# ADR-005: Mann–Whitney U + bootstrap CI on median difference instead of naive percentage

* **Status:** Accepted
* **Date:** 2026-09-02

## Context

Every load-testing tool has a `compare` command. Most of them say "candidate is 12 % slower
than baseline" and stop there. That is meaningless without a significance test: latency is
noisy, especially at the tail, and 12 % is well within the run-to-run variability of most
systems on most hardware.

We need a defensible verdict: **improved / regressed / no significant change** — with the
statistical machinery to back it up.

## Options considered

### Naive percentage
* Pros: trivial.
* Cons: statistically meaningless. Rejected.

### t-test on the means
* Pros: familiar, quick.
* Cons: latency distributions are heavily skewed (long right tail); the sample mean is not
  a good summary; the t-test's normality assumption is violated.

### Mann–Whitney U (chosen, primary)
* Pros: non-parametric — no distribution assumption. Compares whether one sample tends to
  produce larger values than the other by ranking all observations across both groups. Well-
  established. Robust to outliers.
* Cons: pure "greater/less" verdict — doesn't quantify the effect size directly.

### Bootstrap 95 % CI on the median difference (chosen, secondary)
* Pros: gives an effect-size interval. If the CI excludes zero, the difference is significant;
  the CI's magnitude tells you how big the difference is. Deterministic with a fixed seed.
* Cons: computationally heavier than a closed-form test. Not a problem at the sample sizes
  we deal with (thousands, not millions).

### Report both, verdicts must reconcile
* Pros: the reader gets a rank-based verdict *and* an effect-size interval. Disagreement is
  itself informative.
* Cons: two numbers, two verdicts. The comparison report explicitly reconciles them and takes
  the more conservative one on disagreement.

## Decision

Compute both. Mann–Whitney U (two-sided, tie-corrected normal approximation) is primary; the
bootstrap CI is secondary and quantifies the effect. The report states both verdicts and the
overall verdict:

- Both agree → overall = that verdict.
- Mann–Whitney says NoSignificantChange but bootstrap disagrees → overall = bootstrap
  (small dataset case where U's normal approximation is under-powered).
- Mann–Whitney says Improved/Regressed and bootstrap disagrees → overall = Mann–Whitney
  (rank test is more robust to outliers).

The CLI exit code is 1 if the overall verdict is `Regressed`, else 0.

## Consequences

* Unit-tested against known distributions: `SignificanceTest_MannWhitney_IdenticalDistributions_NoChange`,
  `SignificanceTest_MannWhitney_ShiftedDistribution_DetectsChange`,
  `SignificanceTest_Bootstrap_ContainsZero_ForSameDistribution`,
  `SignificanceTest_Bootstrap_ExcludesZero_ForShifted`.
* Bootstrap iterations default to 1500, seed 42 — deterministic reports.
* Reports show the p-value, the z-score, the medians, the delta, and the CI bounds — the
  reader can defend the verdict.

## Risks

* Bootstrap results depend on the seed. **Mitigation:** the seed is a scenario-level constant
  and is reported in the JSON output.
* At very small sample sizes both tests are under-powered. **Mitigation:** the report warns
  when `min(nA, nB) < 20` samples.

## Alternatives

* Permutation test: mathematically equivalent to bootstrap for our purposes but slower. Not
  chosen for that reason alone.
* Bayesian A/B (e.g. Beta prior on error rate, log-normal on latency): more powerful for
  small samples but adds a doctrinal debate that isn't worth having in a portfolio piece.
