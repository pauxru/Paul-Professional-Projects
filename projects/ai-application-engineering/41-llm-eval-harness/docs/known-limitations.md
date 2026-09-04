# Known limitations

Written to be read by someone deciding whether to trust this. It errs toward
overstating the weaknesses.

## 1. No LLM is called anywhere in this repository

The models and judges are simulated. This is a deliberate design decision with
a full argument in `docs/adr/001-simulate-the-systems-under-test.md`, and it is
the right decision for what this project claims, but it has hard consequences:

- **Nothing here establishes anything about any real model's quality.** Not
  GPT, not Claude, not a fine-tune. The absolute numbers are properties of the
  simulation's parameters.
- **The claims are about methods, not models.** "A 50-item eval set has 50.9%
  power to detect +0.05" is a statement about the arithmetic of paired
  comparison given a particular variance. It transfers to a real eval set only
  to the extent that the variance transfers.
- **The variance is the thing to check first.** Every power number scales with
  the paired standard deviation, measured here as 0.171. A team wanting to
  apply these results should measure their own by scoring two systems on the
  same items and taking the standard deviation of the differences. That number
  is cheap to obtain and almost nobody has it.

## 2. The simulated judge is much simpler than a real one

`SimulatedJudge` models additive bias, a length preference, and a two-component
error term. Real LLM judges also exhibit position bias, self-preference,
sensitivity to formatting, non-transitive preferences, and drift as the
provider updates the model. None of that is here.

The consequence is directional and worth stating: **the report understates the
problem.** Section 9 shows a length-biased judge reporting +0.0711 for a true
effect of +0.0021. A judge with four correlated biases instead of one would do
worse, not better.

## 3. `required_n` and `minimum_detectable_effect` assume normality

Both use the normal approximation:

```
n = ((z_alpha/2 + z_beta) * sd / effect)^2
```

Item-level scores are bounded in [0, 1] and often bimodal, so the paired
differences are not normal. The approximation is adequate in the middle and
degrades at small n.

This is measurable rather than hypothetical, and it is measured: section 1
compares the closed-form power against a simulation of the same scenario. They
agree to within about two percentage points at n >= 50 and diverge below that.
**Treat `required_n` output below about 30 as indicative only.**

There is a circularity worth naming: the simulation used to check the
approximation draws from the same generative model the approximation is being
applied to. It validates the arithmetic, not the distributional assumption.

## 4. The BCa bootstrap is unreliable below about n = 30

Section 13 measures coverage of a nominal 95% interval: 91.0% at n = 20, 92.2%
at n = 60, 95.8% at n = 200. BCa is the best of the four procedures compared at
every size, and at n = 20 it is still four points short of its nominal level.

Worse, the acceleration estimate uses jackknife influence values, which are
themselves unstable at small n. There is an explicit degeneracy guard that
falls back to the percentile bootstrap when the bootstrap distribution is
effectively constant or when the BCa transformation approaches its pole -- and
that guard exists because the first version was unreachable in floating point
(see `docs/portfolio/04-bugs-the-experiment-found.md`, bug 3).

**Below n = 30, treat any interval here as approximate.**

## 5. The MDE is least reliable exactly where it matters most

`UNDERPOWERED` is decided by comparing the observed effect to the minimum
detectable effect, which is computed from the *observed* paired standard
deviation. At small n that standard deviation is a poor estimate -- and small
n is precisely when the verdict fires.

The verdict is therefore noisiest in the regime it exists to serve. Suppressing
it at small n would remove it from the only place it is needed, so it stays,
and this paragraph is the disclosure.

## 6. Slice analysis is limited to four fixed tiers

`compare()` analyses `easy`, `medium`, `hard`, `adversarial`. Real evaluation
needs slicing by customer, locale, document type, length, and whatever
dimension the last incident happened along.

Arbitrary slicing is a straightforward extension of the existing code and is
deliberately not implemented, because the number of tests grows with the
number of slices and the multiple-comparisons cost grows with it. Section 5
measures that cost: 20 uncorrected comparisons produce at least one false
positive 41.8% of the time. Shipping arbitrary slicing without forcing the
caller to confront that number would make the tool worse.

## 7. Everything assumes a scalar score per item

A single number per item, comparable across items and averageable. Much real
evaluation is not that shape: pairwise preferences, rubrics with several
dimensions, pass/fail with partial credit, free-text critique.

The pairwise case in particular has a different and better-developed
statistical treatment (Bradley-Terry and its relatives) which is not
implemented here.

## 8. The report's own predictions are not blind

The predictions in `docs/results.md` were written before the corresponding
measurement was read, and `report.py` enforces that ordering mechanically: a
finding with no open prediction raises, and closing the report with an
unresolved prediction raises. Three of seventeen predictions were wrong and
all three are marked in place.

What that does **not** establish is that no prediction was ever revised after
an early exploratory run. The honest description is "written before the
measurement was taken, by someone who had already built the simulation and had
intuitions about it," not "pre-registered." A stronger protocol would commit
the predictions to a separate file, hash it, and have the run verify the hash.

## 9. Single-machine, single-environment

Numbers were produced on CPython 3.12 with numpy 2.5.2 on Windows. The report
is byte-reproducible on that configuration and a test asserts it. Floating
point summation order may differ under a different numpy build, which would
change trailing digits and fail the hash check. That test failing on a
different machine is expected and is not evidence of a bug.

## 10. What would strengthen this most

In order:

1. **One real-model section.** Run one comparison against a live model and
   report the paired standard deviation. That single number would let every
   power figure here be rescaled to reality, and it is the cheapest possible
   validation.
2. **Real human labels for the judge sections.** The judge-agreement work
   assumes a human ground truth that is itself simulated.
3. **A pre-registration file** with a verified hash, closing the gap in item 8.
4. **Arbitrary slice definitions**, with the multiple-comparisons correction
   made unavoidable rather than optional.
