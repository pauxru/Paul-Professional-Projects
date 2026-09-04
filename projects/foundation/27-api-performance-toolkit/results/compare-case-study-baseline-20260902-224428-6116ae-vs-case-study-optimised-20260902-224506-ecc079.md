# Comparison — case-study-baseline vs case-study-optimised

Baseline run: `case-study-baseline-20260902-224428-6116ae` — 2026-09-02 22:44:28 UTC
Candidate run: `case-study-optimised-20260902-224506-ecc079` — 2026-09-02 22:45:06 UTC

## Summary

| Metric | Baseline | Candidate | Delta | % change |
|---|---|---|---|---|
| p50 (ms) | 33.75 | 6.68 | -27.06 | -80.2% |
| p95 (ms) | 66.62 | 24.26 | -42.36 | -63.6% |
| p99 (ms) | 97.43 | 38.46 | -58.96 | -60.5% |
| intended p95 (ms) | 142.00 | 73.00 | -69.00 | -48.6% |
| throughput (rps) | 120.73 | 120.09 | -0.64 | -0.5% |
| error rate | 0.00% | 0.00% | 0.00% | 0.0% |

## Statistical significance

**Mann–Whitney U (two-sided, tie-corrected normal approximation)**

- U1 = 811.00, U2 = 1.00
- z-score = -6.457
- p-value = 0.0000
- Verdict: **Improved**

**Bootstrap CI on median difference (candidate minus baseline)**

- median baseline = 61.86
- median candidate = 22.53
- median delta = -39.32
- 95% CI = [-45.20, -34.41]
- Verdict: **Improved**

## Overall verdict

**Improved** — both tests agree.
