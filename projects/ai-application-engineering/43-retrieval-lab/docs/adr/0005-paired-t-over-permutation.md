# ADR 0005: Paired t-test as the primary significance instrument, with the permutation test kept as a resolution-limited cross-check

## Status

Accepted. Supersedes an earlier, unrecorded decision to use a paired
permutation test alone.

## Context

The grid is 7 chunkers x 5 retrievers = 35 configurations, giving 595 pairwise
comparisons, each over the same 273 queries. Two statistical choices follow:
how to test one pair, and how to control the family-wise error rate over 595 of
them.

Per-query nDCG differences in retrieval are badly behaved. Most are exactly
zero — the two systems returned the same chunk at rank 1 — and the remainder
has a heavy, asymmetric tail. That is a textbook argument for a randomisation
test, which assumes nothing about the shape of the difference distribution, and
the lab originally used a paired permutation test with B = 4,000 resamples.

The correction is Holm's step-down, chosen over Bonferroni for uniform power
gain at identical family-wise guarantee, and over Benjamini–Hochberg because
the report is used to make single "which configuration do I ship" decisions,
where controlling the probability of *any* false claim is the right target
rather than the expected proportion of them.

Composing those two defensible choices produced a table with **zero**
significant differences after correction.

## The defect

A permutation test estimates p by counting resamples at least as extreme as the
observed statistic. With B resamples and the standard add-one correction, the
smallest value it can ever report is 1/(B+1). At B = 4,000 that is 2.50e-04.

Holm's strictest threshold, applied to the smallest p-value in a family of m,
is alpha/m. At alpha = 0.05 and m = 595 that is 8.40e-05.

2.50e-04 > 8.40e-05. **No comparison in the family could clear the threshold
regardless of its effect size.** The zero was arithmetic, not retrieval.

What makes this worth an ADR rather than a bug-fix commit is that the failure
is *invisible*. "Nothing was significant after multiple-comparison correction"
is exactly what an honest, adequately powered, genuinely null experiment also
produces. There is no error, no warning, and no anomaly in the output. The
result reads as a finding, and a careful reader would have no reason to doubt
it.

## Decision

1. The primary reported test is a **paired Student's t-test** on per-query
   differences, with p computed from the incomplete beta function
   (`stats.betai`) so there is no SciPy dependency and the implementation can be
   checked against published critical values in the test suite.
2. The paired permutation test is **retained and reported beside it** in
   section 3a, with its floor stated, as the demonstration of the constraint.
3. `stats.permutation_resolution(B)` and `stats.iterations_for_holm(m, alpha)`
   exist so the arithmetic is a callable function rather than a fact somebody
   has to remember, and both are pinned by tests.
4. The report states the general rule for readers to carry away:
   **before nesting a randomisation test inside a family-wise correction, check
   that 1/(B+1) < alpha/m.**

## Rationale for the t-test specifically

The distributional objection to the t-test is real but weak *here*: n = 273 per
comparison, the statistic is a mean of bounded quantities in [-1, 1], and the
central limit theorem applies to the sampling distribution of that mean even
though the per-query differences are far from normal. The zero-inflation
reduces the effective sample size but does not bias the estimate. With p-values
as small as 3.56e-78 in the observed data, no plausible amount of
tail-behaviour mis-modelling moves a comparison across a 8.40e-05 threshold.

The alternative — raising B to the ~11,900 the correction requires — costs
roughly three times the current resampling budget on every one of 595
comparisons, for a result that section 3a shows agrees with the t-test on raw
significance to within one comparison (537 vs 536).

## Consequences

- Section 3's table changed from "0 significant after correction" to "491 of
  595". The narrative changed with it: the finding is no longer "most of the
  grid is noise" but "pairing recovers more power than correction removes", and
  the original prediction is now marked contradicted.
- The report carries an extra section whose subject is the instrument rather
  than the systems. That is deliberate; it is the most transferable content in
  the file.
- `stats.py` now contains a parametric test, which is a dependency on an
  assumption the permutation test did not need. The assumption is documented in
  the module docstring rather than left implicit.

## Alternatives considered

**Raise B to 12,000 and keep the permutation test.** Rejected on cost, and
because it fixes this instance while leaving the general trap undocumented.

**Switch to Benjamini–Hochberg.** Would have raised the threshold enough to
hide the problem in this instance, since BH's least strict threshold is alpha
rather than alpha/m. Rejected: it changes what is being controlled in order to
make a symptom go away, and the next grid with a larger m reproduces the bug.

**Report uncorrected p-values with the family size stated.** Rejected. Section
3's entire point is that the naive analysis and the defensible one disagree
about *which* comparisons are real, not merely how many.
