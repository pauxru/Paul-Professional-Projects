# Your judge has 92% agreement and is worse than a hard-coded string

The standard way to validate an LLM judge is to sample some items, have a human
label them, and compute agreement. Perhaps you are careful and compute Cohen's
kappa rather than raw agreement. Perhaps you are very careful and know that
kappa misbehaves under skew, so you compute Gwet's AC1 as well.

Here is a judge, measured on 4,000 items drawn from production traffic that
passes 92% of the time:

| statistic | value |
|---|---|
| raw agreement | 93.0% |
| Cohen's kappa | 0.540 |
| Gwet's AC1 | 0.885 |

The kappa is mediocre, the AC1 is strong, and the standard reading of that
pattern -- prevalence is suppressing kappa, trust AC1 -- is correct. Prevalence
*is* suppressing kappa. The prevalence index is 0.777.

Now the number nobody computes:

| | |
|---|---|
| judge's agreement with humans | 93.0% |
| **agreement from answering "pass" every time** | **92.6%** |
| margin | **+0.0040, 95% CI [-0.0073, +0.0145], p = 0.51** |

On four thousand items, this judge is not measurably better than a hard-coded
string. It costs money, adds latency, and reproduces a constant.

At 97% prevalence the same judge is measurably **worse** than not having one:
a margin of -0.0450 with an interval that excludes zero. It still shows over
90% agreement and an AC1 of 0.885.

## Why both statistics miss this

Cohen's kappa and Gwet's AC1 disagree about how to model chance agreement, and
that disagreement is the entire literature on the kappa paradox. But they agree
on the question. Both ask:

> How much better than **random guessing** is this labeller?

The question that decides whether to deploy a judge is:

> How much better than **not having one** is this labeller?

Those coincide only when the classes are balanced. At 92% prevalence, random
guessing gets 50% and the trivial constant gets 92.6%, and the gap between
those two baselines is where a useless judge hides.

AC1 is the sharper illustration. It was *specifically constructed* so that
prevalence does not collapse its chance term -- that is its reason for
existing, and it succeeds. The same construction means degeneracy does not
collapse it either. **A judge that returns "pass" for every single item scores
AC1 above 0.94 on 95%-pass data.** The statistic is not broken. It is
answering its question correctly. Its question is not the deployment question.

## The algebra, which is worth seeing

Write the confusion matrix as `(a, b, c, d)` for (both pass, judge-only pass,
human-only pass, both fail). The judge is right on `a + d`. On pass-majority
data the constant labeller is right on `a + c`. So the margin is

```
margin = (a + d)/n - (a + c)/n = (d - c)/n
```

and **`a` does not appear**.

Every item where the human passed and the judge agreed contributes exactly
nothing to the judge's case, because a hard-coded string got that item too. On
a 92%-pass dataset that is 92% of the items -- and they are the items that
produce the agreement percentage in the report.

The judge's entire value lives in the minority class. This is obvious once
written down and is invisible in every statistic in the first table.

There is a bonus. `(d - c)` is a count of discordant pairs, so under the null
that the judge is no better than the constant, each discordant item is a fair
coin and the exact distribution is binomial. The p-value above needs no
resampling. (The implementation has a bootstrap too. It was written first, it
agrees everywhere, and it stays as the evidence that they agree.)

## How this fails in practice

The failure mode is not that someone deploys an always-yes judge. It is
subtler and much more common.

A team validates their judge on a **balanced** sample -- 100 items, half known
good, half known bad, because that is the sensible way to build a validation
set. On that sample the judge genuinely is skilful: 92.1% agreement against a
constant labeller's 50.6%, a margin of +0.4157. It passes every check
including this one.

They then run it on production traffic, which is 92% passes.

Nothing about the judge changed. Everything about its usefulness did. The
validation was performed on a distribution that does not exist in production,
and the statistic that would have revealed the difference -- the margin over
the best constant -- is the one that was not computed, because none of the
recommended statistics include it.

| pass rate | judge agreement | constant labeller | margin | useful? |
|---|---|---|---|---|
| 50% | 92.1% | 50.6% | +0.4157 | yes |
| 70% | 92.0% | 69.8% | +0.2225 | yes |
| 85% | 91.9% | 85.5% | +0.0648 | yes |
| 92% | 93.0% | 92.6% | +0.0040 | **no** |
| 97% | 92.6% | 97.1% | -0.0450 | **actively harmful** |

Read the "judge agreement" column alone -- which is what a dashboard shows --
and the judge is equally good in all five worlds.

## What to do

Three numbers, not one:

1. **Agreement with humans**, on a sample drawn from the *deployment*
   distribution rather than a balanced one.
2. **Agreement of the best constant labeller** on that same sample. One line
   of code.
3. **The margin, with an interval or an exact p-value.** It is an estimate
   like any other.

Point 3 is not pedantry, and I know that because the first version of this
check in my own code did not do it. It compared the two point estimates and
declared a winner on any positive difference -- which passed the 92% judge on
a margin of +0.004 with an interval straddling zero. Replacing one unreliable
number with a second unreliable number is not a fix, and the error survived
inside the very property written to detect that error.

Judge validation is a measurement. It has the same statistical structure as
comparing two systems -- both are paired on items, both are estimates with
uncertainty, both are routinely reported as bare percentages. Teams that would
never compare two prompts on 40 items will validate a judge on 40 items and
quote the result to three decimals for a year.
