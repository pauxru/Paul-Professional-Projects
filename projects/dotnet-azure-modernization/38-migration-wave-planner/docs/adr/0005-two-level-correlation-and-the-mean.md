# 5. Two levels of correlation, and the mean rather than the median

**Status:** accepted

## Context

The schedule model needs uncertainty in team effort. Three choices had to be
made and each of them turned out to change a conclusion.

## Decision 1: log-normal, median-corrected

Team effort is `estimate * exp(sigma * z - sigma^2 / 2)` with `sigma = 0.35`.

Log-normal because effort cannot be negative and overruns have a long right
tail. The `- sigma^2/2` term is the part that matters: a log-normal with
median `m` has *mean* `m * exp(sigma^2/2)`, which at `sigma = 0.35` is 6.3%
above the estimate. Without the correction, every team's expected effort would
be 6.3% higher than its point estimate and the reported schedule bias would
be mostly that inflation.

The whole of section 6 rests on this. The claim is "the plan date is
optimistic *even when every individual estimate is unbiased*", and that is
only a claim about merging parallel work if the estimates really are unbiased.
`tests/test_risk.py` asserts the corrected multiplier has mean 1.0 over
400,000 draws, and separately asserts the *uncorrected* one does not — an
invariant with no counterexample test is a comment.

## Decision 2: correlation at two levels, not one

`z[w,t] = sqrt(a)·G + sqrt(b)·W_w + sqrt(1-a-b)·E_wt`

`G` is a programme-wide shock, `W_w` a per-wave shock, `E_wt` idiosyncratic.
Correlation between two teams in the same wave is `a + b`; between teams in
different waves it is `a`. Defaults `a = 0.25`, `b = 0.20`.

A single correlation parameter would have made section 8 impossible, and
section 8 contains the finding that justifies the extra complexity: **the two
levels push in opposite directions.**

- Programme-level correlation widens the distribution. A shock that hits
  everything cannot be averaged away across waves, so the tail lengthens.
- Wave-level correlation *shortens* the tail. A wave's duration is the maximum
  over its teams; correlating those teams makes the maximum behave more like a
  single draw and less like a max of independents, so the merge bias shrinks.

With one parameter these cancel to an uninterpretable middle. The P90 across
the sweep ranges 13.59–15.15 months — a 1.57-month spread driven entirely by
an assumption nobody in the programme has data for, which is larger than most
of the effects the model is being used to compare. That is the finding.

## Decision 3: the headline statistic is the mean, not the P50

This one was forced by a test failure and it is the most useful thing in the
section.

`RiskResult.merge_bias` was originally `P50 - deterministic`. A test asserted
it was positive. It came back exactly `0.0`.

Change freezes forbid cutovers in months 2, 10 and 11. A draw whose
unconstrained end lands anywhere inside a run of frozen months is pushed to
the same date. Months 10 and 11 are adjacent, so **everything landing anywhere
in a two-month window lands on exactly month 12**. The outcome distribution is
mixed, not continuous, and it carries an atom of probability as wide as the
freeze.

On the `dependents_first` plan, 30.4% of all outcomes land on exactly month
12.00. The median falls inside that atom. `P50 - deterministic` reads **+0.00
months** while the mean slip is **+0.84**.

So:

- `merge_bias` is now `mean - deterministic`, which is also the statistic that
  matches the statement being made (`E[max] > max[E]`);
- `median_slip` is reported alongside it rather than instead of it;
- `RiskResult.largest_atom()` and `median_in_atom()` exist so the report can
  *show* the quantisation instead of asserting it;
- the report's second prediction in section 6 — that the two statistics would
  roughly agree — is recorded as contradicted, with the per-plan table that
  contradicts it.

There is a second-order effect the table also exposes: freezes make the
*reported* bias smaller while making the actual date later, because the
deterministic plan has already absorbed the slippage the simulation would
otherwise have discovered (plan date 9.90 without freezes, 12.00 with).

## Consequences

**A whole class of programme reporting is suspect.** Any programme quoting a
P50 against a freeze calendar, a quarterly release train, or any other
quantised delivery constraint is quoting a statistic that can read zero slip
while the distribution is months late. This is not exotic — enterprise
delivery is full of quantisation.

**Sigma is not calibrated.** 0.35 is a plausible figure for software effort
estimation, not a measured one for this organisation. It is the single largest
lever on section 6 and is named first in `docs/known-limitations.md`.

**The freeze loop is slow.** `simulate` applies freeze slippage in a Python
loop over waves with a list comprehension across draws, because the slippage
is a fixed point rather than an arithmetic operation. At 40,000 draws over
five waves it is the dominant cost of the report. A vectorised version is
possible and is not worth the risk of getting it subtly wrong for a 45-second
report.
