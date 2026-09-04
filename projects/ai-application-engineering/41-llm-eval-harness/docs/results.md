# LLM Evaluation: what the numbers can and cannot support

Everything here is measured, not asserted. The systems and judges are
simulated with known parameters, which is the point: the claims are about
whether a method can recover a truth, and that requires knowing the truth. No
LLM is called anywhere in this repository. The README section "Why the systems
are simulated" and `docs/adr/001` explain why a real model would make this
argument weaker rather than stronger; `docs/known-limitations.md` states
plainly what it therefore does not establish.

Each section states a prediction in writing before the corresponding
measurement is read. The report generator enforces the ordering: a measurement
with no outstanding prediction raises, and so does closing the report with one
unresolved.


## 1. What a 50-item eval set can actually see

Fifty items is a common size for a hand-curated eval set. It is large enough
to feel serious and small enough to review by hand, which is how sizes get
chosen. Nobody computes what it can detect.

The quantity that decides this is not the eval set size on its own. It is the
size relative to the standard deviation of the *paired difference* between the
two systems -- which depends on how noisy the items are, how noisy the judge
is, and how correlated the two systems are with each other. All three are
measurable and none are measured in practice.

With the simulated systems used throughout this report, the observed standard
deviation of paired differences on a 50-item set is 0.1786. Everything below
follows from that one number.

**Predicted.** A 50-item eval set has less than 25% power to detect a true 5% quality
improvement -- so more than three times in four, a real improvement of that
size will be reported as no change.

**Found — prediction wrong.** Power at n=50 is 50.9% -- roughly a coin flip, not the under-25% predicted.
The prediction was made from the folk belief that small eval sets are
hopeless, and the folk belief is miscalibrated in the same way as the practice
it criticises: it asserts a number without computing one.

What the prediction got wrong is the standard deviation. It implicitly assumed
something near 0.28 -- the value you get if the two systems are statistically
independent. They are not; they share item difficulty, and the measured paired
sd is 0.1786. The correlation between the systems is doing more work than the
eval set size.

That is the finding worth keeping, and it is more useful than the one
predicted. Whether a 50-item eval set is adequate is not a property of the
number 50. It is a property of how similar the two things being compared are,
which changes every time you compare a different pair, and which no team
measures. The same eval set is adequate for comparing two prompt variants and
hopeless for comparing two different model families.

It is still bad. A real 5% improvement is reported as no change 49.1% of the
time, and the practical consequence is not that the team misses improvements
but that they stop believing the eval, because it disagrees with what they can
see by eye -- and then revert to shipping on vibes, which is where they
started.

| n    | power to see +0.05 | exaggeration if significant | sign errors |
|------|--------------------|-----------------------------|-------------|
| 25   | 30.5%              | 1.80x                       | 0.33%       |
| 50   | 50.9%              | 1.39x                       | 0.02%       |
| 100  | 80.0%              | 1.12x                       | 0.00%       |
| 200  | 97.6%              | 1.01x                       | 0.00%       |
| 500  | 100.0%             | 1.00x                       | 0.00%       |
| 1000 | 100.0%             | 1.00x                       | 0.00%       |

The eval set has to reach roughly 200 items before it detects a 5% improvement
four times out of five.


### The same eval set, a different comparison

If the contradiction above is right, then the adequacy of an eval set is not a
property of the eval set. Testing that requires holding the eval set fixed and
changing only how similar the two systems are.

**Predicted.** Holding the 50 items fixed and comparing two systems that share little
item-level structure, instead of two variants of one system, will cut the
power to detect the same 5% improvement by at least a third -- from the same
eval set, on the same day, with nothing about the evaluation changed.

**Found.** Power falls from 79.0% to 28.5% across the range -- a 63.9% reduction -- with
the eval set, the judge, the items, and the true effect all held exactly
constant.

So "our eval set has 50 items" is not a statement about measurement
capability, and neither is "our eval set is too small". The capability has to
be computed per comparison, which is why the harness reports a minimum
detectable effect on every single verdict rather than once in a README.

| what is being compared       | shared variance | paired sd | power at n=50 | detectable effect at n=50 |
|------------------------------|-----------------|-----------|---------------|---------------------------|
| two variants of one prompt   | 0.85            | 0.1275    | 79.0%         | 0.0505                    |
| two prompts, same model      | 0.60            | 0.1807    | 50.0%         | 0.0716                    |
| same family, different size  | 0.30            | 0.2256    | 35.3%         | 0.0894                    |
| two different model families | 0.05            | 0.2588    | 28.5%         | 0.1025                    |


## 2. The results that do clear significance are inflated

Low power is usually described as a risk of missing things. That is the
harmless half. The damaging half is what it does to the findings that survive.

To clear a significance threshold in a small sample, an estimate has to be
large. So the estimates that clear it are not a random sample of all estimates
-- they are the upper tail. Every significant result from an underpowered eval
is therefore systematically larger than the truth, and the lower the power,
the worse the inflation. This is the Type M error, and it explains a specific
and very familiar experience: the improvement that measured +8% in the eval
and delivered nothing in production.

**Predicted.** At the power level of a 50-item eval set, statistically significant estimates
of a true 5% effect will average at least 1.8x the true effect, and the
exaggeration will grow as the true effect gets smaller.

**Found.** At a true effect of +0.05 the exaggeration ratio is 1.32x: the significant
results report an average improvement of +0.066 for a change that is actually
+0.050. Below that the inflation gets worse, reaching 5.41x for the smallest
effect tested.

This is the mechanism behind eval scores that do not reproduce. Nobody is
cheating and no measurement is wrong; the filter that selects which
measurements get reported is doing the damage.

| true effect | power | exaggeration | reported as | sign errors |
|-------------|-------|--------------|-------------|-------------|
| +0.100      | 98.8% | 1.01x        | +0.101      | 0.00%       |
| +0.050      | 57.0% | 1.32x        | +0.066      | 0.00%       |
| +0.030      | 25.5% | 1.97x        | +0.059      | 0.35%       |
| +0.020      | 14.5% | 2.82x        | +0.056      | 2.48%       |
| +0.010      | 7.7%  | 5.41x        | +0.054      | 13.11%      |

> The sign-error column is the one to read twice. At a true effect of +0.01,
> 13.1% of the statistically significant results point the wrong way -- a
> confident, interval-backed claim that the change helped, for a change that
> hurt.


## 3. How large the eval set has to be

Turning the previous section around: given that you want to detect a
regression of a particular size, how many items do you need? This is the
calculation that should happen before an eval set is built, and it takes one
line.

**Predicted.** Detecting a 1% change will require more than 20x the items needed for a 5%
change, because required sample size scales with the inverse square of the
effect.

**Found.** Detecting +0.01 needs 2309 items against 93 for +0.05, a factor of 25x. The
quadratic is unforgiving: every halving of the effect you want to see costs
four times the eval set.

This is the number that should end the argument about whether to invest in
more eval items. A team that wants to detect 1% regressions and has 50 items
is not close; they are off by 46x.

| effect to detect | items required (80% power) | MDE at n=50 | MDE at required n |
|------------------|----------------------------|-------------|-------------------|
| +0.100           | 24                         | 0.0679      | 0.0981            |
| +0.050           | 93                         | 0.0679      | 0.0498            |
| +0.030           | 257                        | 0.0679      | 0.0300            |
| +0.020           | 578                        | 0.0679      | 0.0200            |
| +0.010           | 2309                       | 0.0679      | 0.0100            |


## 4. Why paired analysis is worth more than a bigger eval set

Two systems evaluated on the same items share a large nuisance term: a
question that is hard for one is usually hard for the other. Comparing
per-item differences cancels it. Comparing group means does not.

How much this is worth depends on the correlation between the two systems'
per-item scores, which for two variants of the same prompt is high. The point
of stating it as a measurable parameter is that a team can estimate it from a
single run they have already done.

**Predicted.** At a realistic system correlation, the paired interval will be at least 30%
narrower than the unpaired one, which is equivalent to roughly doubling the
eval set for free.

**Found.** At a shared-variance fraction of 0.60 -- which produced a measured score
correlation of 0.735 -- the paired standard deviation is 0.1555 against 0.3018
unpaired. That is a 1.94x narrower interval, equivalent to 3.8x the eval
items, for no additional model calls at all.

| shared variance | measured score correlation | paired sd | unpaired sd | interval narrowing | equivalent items |
|-----------------|----------------------------|-----------|-------------|--------------------|------------------|
| 0.00            | 0.484                      | 0.2304    | 0.3205      | 1.39x              | 1.9x             |
| 0.30            | 0.593                      | 0.1967    | 0.3083      | 1.57x              | 2.5x             |
| 0.60            | 0.735                      | 0.1555    | 0.3018      | 1.94x              | 3.8x             |
| 0.90            | 0.886                      | 0.1000    | 0.2960      | 2.96x              | 8.8x             |

> The first row is the one worth noticing. Even at zero *model* correlation the
> measured score correlation is 0.484, because the eval set has difficulty tiers
> and both systems find the hard tier hard. The structure of the eval set alone
> supplies pairing benefit; a flat undifferentiated eval set supplies less.


## 5. Twenty prompt variants, none of them better

The characteristic workflow of prompt engineering is to try many variants and
keep the one with the best score. The characteristic statistical property of
that workflow is that it will find a winner whether or not one exists.

Here twenty variants are generated with a true effect of *exactly zero* --
they are the same system, differing only in random seed. Each is compared
against the baseline on a 50-item eval set.

**Predicted.** With 20 comparisons at alpha = 0.05 and no real effect anywhere, at least one
will look significant about 64% of the time, which is 1 - 0.95^20.

**Found — prediction wrong.** Across 400 simulated sweeps, 41.8% produced at least one variant that looked
significantly better or worse than baseline at p < 0.05. Not one of them
differed from baseline in any way. But the predicted figure was 64.2%, and the
measured rate is well below it.

The shortfall is not a bug, and it is more interesting than the prediction
was. The textbook 1 - (1-alpha)^k assumes the k tests are independent. A
prompt sweep is not: all twenty variants are compared against the *same*
baseline run, so a baseline that happened to score high on this eval set
pushes all twenty comparisons in the same direction at once. The tests are
positively correlated, and positively correlated tests produce fewer families
with at least one false positive than independent ones do.

That figure of 64% is quoted constantly, including in the docstring of this
repository's own Benjamini-Hochberg implementation before this experiment was
run. It is the wrong number for the situation everyone quotes it about.

**Predicted.** If the shortfall is caused by the shared baseline, then giving each of the
twenty comparisons its own independent baseline draw -- changing nothing else
-- will push the rate back up towards the textbook 64%.

**Found.** With an independent baseline per comparison the rate rises from 41.8% to 68.8%
against a theoretical 64.2%. The shared baseline was the whole of the
difference.

The confirming detail is the second column. The average number of false
positives per sweep barely moves -- 0.98 shared against 1.04 independent --
while the proportion of sweeps containing at least one moves by 27.0%. That is
the signature of correlation and not of anything else: correlation does not
change how many errors you make, only how they clump. The shared baseline
gathers a sweep's errors into the same sweep.

The practical lesson runs the other way from what you might expect. Sharing a
baseline makes the *family-wise* error rate look better while leaving the
error count untouched, so the one variant you pick out of the sweep is no more
trustworthy than before. You are simply more likely to get all your errors on
the same afternoon.

| procedure                           | sweeps with >=1 false discovery | false positives per sweep |
|-------------------------------------|---------------------------------|---------------------------|
| raw p < 0.05, shared baseline       | 41.8%                           | 0.98                      |
| raw p < 0.05, independent baselines | 68.8%                           | 1.04                      |
| Benjamini-Hochberg (FDR 5%)         | 5.2%                            | 0.14                      |
| theoretical, independent tests      | 64.2%                           | 1.00                      |

Benjamini-Hochberg brings the rate of sweeps containing any false discovery
down to 5.2%, against the nominal 5%, and cuts false positives per sweep from
0.98 to 0.14.

> The correction is four lines of code. The absence of it is the single most
> common defect in LLM evaluation practice, and it is invisible because the
> output of a broken sweep and a correct one look identical: a table of variants
> with one highlighted.


## 6. The winner of a sweep does not reproduce

Selecting the maximum of twenty noisy measurements produces a number biased
upwards by construction, whether or not the selected variant is any good. The
question a team should ask is not "is the winner significant?" but "how much
of the winner's margin survives a fresh eval set?"

**Predicted.** The best of twenty identical variants will show a substantial apparent
improvement on the eval set it was selected on, and essentially all of that
improvement will vanish when the same variant is re-measured on a held-out
set.

**Found.** On the set it was selected on, the winning variant showed an average
improvement of +0.0305. Re-measured on a held-out set of the same size, the
same variant delivered -0.0022 -- 107.1% of the apparent gain evaporated,
which is the correct amount, since there was never any gain to begin with.

A held-out eval set is not a nicety. It is the only thing standing between a
prompt sweep and a quarter of imaginary progress.

| measurement                      | mean apparent improvement |
|----------------------------------|---------------------------|
| best of 20, on the selection set | +0.0305                   |
| same variant, held-out set       | -0.0022                   |
| true effect                      | +0.0000                   |


## 7. A judge with 92% agreement can be worthless

The usual way to validate an LLM judge is to have a human label a sample and
report the agreement rate. High agreement is taken as evidence the judge
works. On a skewed dataset it is evidence of almost nothing, because a judge
that says "pass" to everything will agree with a human on a dataset that is
92% passes.

Cohen's kappa is supposed to correct for this by subtracting chance agreement.
It over-corrects: on highly skewed data the chance-agreement term approaches
the observed agreement and kappa collapses toward zero even for a genuinely
good judge. This is the kappa paradox, and it means neither number can be read
alone.

**Predicted.** On a dataset where 92% of items pass, a judge with high raw agreement will
show a Cohen's kappa below 0.4 -- conventionally 'fair' -- while Gwet's AC1
stays high, and the prevalence index will identify skew as the cause.

**Found.** At 92% prevalence the same judge shows 90.8% raw agreement and a Cohen's kappa
of 0.540. Read the kappa alone and the judge is unusable; read the agreement
alone and it is excellent. Gwet's AC1 is 0.885, and the prevalence index of
0.777 names the cause.

The judge did not change across any row of this table. Only the class balance
of the evaluation data did.

| pass rate | raw agreement | Cohen kappa | Gwet AC1 | prevalence index | paradox flagged |
|-----------|---------------|-------------|----------|------------------|-----------------|
| 50%       | 89.5%         | 0.790       | 0.790    | 0.045            | no              |
| 70%       | 91.5%         | 0.806       | 0.849    | 0.355            | no              |
| 85%       | 89.8%         | 0.650       | 0.856    | 0.647            | yes             |
| 92%       | 90.8%         | 0.540       | 0.885    | 0.777            | yes             |
| 97%       | 90.2%         | 0.382       | 0.885    | 0.833            | yes             |

> The operational consequence: a team that validates its judge on a balanced
> sample and then runs it on production traffic -- which is overwhelmingly
> passes -- has validated something other than what it is running.


### The comparison that neither statistic makes

Both kappa and AC1 answer the question "how much better than random guessing
is this judge?". That is not the question. Nobody was going to deploy random
guessing. The alternative to an LLM judge is not chance, it is *answering the
same thing every time*, which costs no tokens, adds no latency and on a
92%-pass dataset is right 92% of the time.

**Predicted.** The judge from the table above -- the one with 90%+ agreement and a
respectable AC1 -- will not have a margin over the constant labeller that is
distinguishable from zero at 92% prevalence, even on four thousand items. Its
entire apparent skill is the skew.

**Found.** At 92% prevalence the judge scores 93.0% against a constant labeller's 92.6%.
The margin is +0.0040, the bootstrap interval is [-0.0073, +0.0145], and the
exact p-value is 0.510. On four thousand items the judge is not measurably
better than a hard-coded string.

Its AC1 of 0.914 says nothing about this, and cannot: AC1 was specifically
constructed so that prevalence does not collapse its chance term, and the same
construction means degeneracy does not collapse it either. A judge that
returns "pass" for every item on this data scores an AC1 above 0.94.

By 97% prevalence the margin is negative and significant: the judge is
measurably worse than not having one, while still showing over 90% agreement
and an AC1 of 0.885.

| pass rate | judge agreement | constant labeller | margin [95% CI]            | exact p | informative | trustworthy |
|-----------|-----------------|-------------------|----------------------------|---------|-------------|-------------|
| 50%       | 92.1%           | 50.6%             | +0.4158 [+0.3987, +0.4328] | 0.000   | yes         | yes         |
| 70%       | 92.0%           | 69.8%             | +0.2225 [+0.2057, +0.2385] | 0.000   | yes         | yes         |
| 85%       | 91.9%           | 85.5%             | +0.0648 [+0.0515, +0.0777] | 0.000   | yes         | yes         |
| 92%       | 93.0%           | 92.6%             | +0.0040 [-0.0073, +0.0145] | 0.510   | no          | no          |
| 97%       | 92.6%           | 97.1%             | -0.0450 [-0.0553, -0.0360] | 0.000   | no          | no          |

Two things in that table were not obvious when the section was written. The
first is that the margin has a closed form. Writing the confusion matrix as
(a, b, c, d) for (both pass, judge-only pass, human-only pass, both fail), the
judge is right on a + d and the constant labeller -- on a pass-majority
dataset -- is right on a + c. So

```text
margin = (a + d)/n - (a + c)/n = (d - c)/n
```

and a does not appear. Every item where the human passed and the judge agreed
contributes exactly nothing to the judge's case, because a hard-coded string
got that item too. On a 92%-pass dataset that is 92% of the items, and they
are the items that produce the agreement percentage in the slide deck.

The second is that once the statistic is written that way it is a count of
discordant pairs, so under the null it is a fair coin on each discordant item
and the exact distribution is binomial. The bootstrap column and the exact-p
column agree everywhere in that table, and the bootstrap costs four thousand
resamples per row to reproduce a number available in closed form. It was
written first, which is the only reason it is still there: it is the evidence
that the two agree.

The property this section tests went through three versions. It started as
`observed > baseline_agreement`, which passed the 92% judge on a margin of
+0.004. Comparing two point estimates and declaring a winner is the exact
error this whole report is about, and it survived in the code that was
supposed to detect it.

> This does not make kappa or AC1 wrong. It makes them answers to a question
> about measurement rather than a question about deployment. The honest report
> of a judge is three numbers -- agreement, a prevalence-robust chance
> correction, and the margin over the trivial baseline -- and the third is the
> one that decides anything.


## 8. Two judges, identical agreement, different eval set sizes

Judge error is usually treated as one quantity. It is two, and they have
opposite consequences for an A/B comparison.

A judge that is *consistently* wrong about an item -- always reading it 0.1
too generously -- applies that error to both systems, and it cancels exactly
in the paired difference. A judge that is *freshly* wrong each time it scores
adds variance that does not cancel. Two judges can have identical agreement
with humans and identical average error while differing substantially in how
many eval items you need.

**Predicted.** A judge whose error is entirely stable per item will require materially fewer
eval items than one whose error is entirely fresh, despite the two having the
same error magnitude and the same agreement with ground truth.

**Found.** The fully-fresh judge needs 228 items to detect a 4% improvement; the
fully-stable judge needs 98, a factor of 2.33x. Raw agreement with ground
truth barely moves across the range, so no agreement statistic would have
distinguished them.

This is measurable without any human labelling at all: score the same
responses twice and look at the variance of the difference. That single number
is worth more for eval design than an agreement study, and almost nobody
computes it.

| fraction of judge error that is stable per item | sd of paired difference | items needed for +0.04 | raw agreement with truth |
|-------------------------------------------------|-------------------------|------------------------|--------------------------|
| 0.00                                            | 0.2156                  | 228                    | 85.5%                    |
| 0.25                                            | 0.2072                  | 211                    | 87.5%                    |
| 0.50                                            | 0.1822                  | 163                    | 90.0%                    |
| 0.75                                            | 0.1713                  | 144                    | 88.0%                    |
| 1.00                                            | 0.1407                  | 98                     | 85.0%                    |


## 9. The failure that more data makes worse

Everything so far has been about variance, and variance is fixed by collecting
more data. The dangerous judge failures are the ones that are not.

LLM judges reliably prefer longer answers. If a prompt change makes the model
more verbose without making it better, the judge will score it higher --
consistently, on every item, in the same direction. That is not noise. Adding
eval items does not average it away; it narrows the interval around a wrong
answer.

**Predicted.** A change with zero true quality effect but 60% longer outputs will be reported
as a significant improvement, and the confidence in that false finding will
*increase* with eval set size rather than decreasing.

**Found.** At n=800 the judge reports an improvement of +0.0711 with an interval of width
0.0237 that excludes zero, for a change whose true quality effect is +0.0021.
The interval narrowed by 5.1x going from 25 items to 800, and every bit of
that narrowing bought more confidence in a false conclusion.

This is the reason a statistics-only answer to eval quality is insufficient.
Confidence intervals quantify sampling error. They say nothing whatsoever
about an instrument that is pointed at the wrong thing, and they will happily
certify it.

| eval items | reported improvement | interval width | significant | true quality change |
|------------|----------------------|----------------|-------------|---------------------|
| 25         | +0.0762              | 0.1201         | yes         | +0.0006             |
| 50         | +0.0611              | 0.1056         | yes         | -0.0161             |
| 100        | +0.0838              | 0.0742         | yes         | +0.0069             |
| 200        | +0.0778              | 0.0469         | yes         | +0.0043             |
| 400        | +0.0739              | 0.0329         | yes         | +0.0028             |
| 800        | +0.0711              | 0.0237         | yes         | +0.0021             |

> The defence is not statistical. It is to hold output length fixed when
> comparing, or to include length as a reported covariate so that a reviewer
> sees the +60% next to the +0.03 and asks the obvious question. The harness
> reports token counts alongside every comparison for exactly this reason.


## 10. The aggregate that hides the regression

A single headline number is the most common eval output and the easiest to
defeat. A change that helps common easy cases and breaks rare hard ones nets
to approximately zero, and the hard cases are usually the ones that generate
incidents.

**Predicted.** A change that improves easy items by 6% and degrades adversarial items by 12%
will show an overall effect small enough to pass an aggregate gate, while
slice analysis will block it.

**Found.** The overall effect is -0.0100 with interval [-0.0298, +0.0102] --
indistinguishable from zero, which an aggregate gate reads as safe. The
adversarial slice is -0.1286 with interval [-0.1746, -0.0845].

The gate returns REGRESSED: the adversarial slice regressed by -0.1286 (CI
-0.1746..-0.0845), which the overall mean of -0.0100 hides

| slice       | n   | baseline | candidate | delta   | interval           | flagged |
|-------------|-----|----------|-----------|---------|--------------------|---------|
| overall     | 300 | 0.6565   | 0.6466    | -0.0100 | [-0.0298, +0.0102] | no      |
| easy        | 75  | 0.8083   | 0.8753    | +0.0670 | [+0.0373, +0.1009] | yes     |
| medium      | 105 | 0.7244   | 0.7332    | +0.0087 | [-0.0229, +0.0402] | no      |
| hard        | 75  | 0.5844   | 0.5425    | -0.0419 | [-0.0829, +0.0032] | no      |
| adversarial | 45  | 0.3653   | 0.2367    | -0.1286 | [-0.1746, -0.0845] | yes     |

> Slices are multiple tests, so they get the same multiplicity correction as
> anything else. Four slices at raw alpha = 0.05 give an 18.5% chance of a
> spurious slice regression per comparison, and a gate that blocks good changes
> one time in five will be switched off within a month.


## 11. Changing the eval set changes the score more than the change does

Eval sets are edited constantly, and usually not recorded. Someone adds
fifteen cases from last week's incident; someone drops an item everyone agreed
was ambiguous. Each edit is defensible on its own.

**Predicted.** Averaged over many baseline models, adding 15 items and reweighting the
difficulty mix of a 50-item eval set will move the measured score by more than
the 0.04 quality improvement this report has been trying to detect -- so a run
labelled 'quality went up' could be entirely explained by an unrecorded
dataset edit.

**Found — prediction wrong.** Averaged over 200 baseline models, the same unchanged model scores -0.0397
differently on v2 than on v1. The model did not change; the ruler did. That
shift is 0.99x the size of a genuine +0.0400 quality improvement, not larger
than the effect, but the same order of magnitude -- so the prediction is not
confirmed as stated.

Either way the operational point stands, and it does not depend on the ratio
exceeding one: an untracked dataset edit produces a score movement
indistinguishable in size from the effects the eval exists to detect, in a
direction nobody chose, with no record that anything happened.

The fingerprints differ (452662841893 vs 2db3634e26ab) and the diff reports 15
added, 0 removed, 5 modified, so `comparable` is False. The gate refuses the
comparison rather than reporting the number.

| comparison                    | mean measured change | true change |
|-------------------------------|----------------------|-------------|
| same model, v1 -> v2 eval set | -0.0397              | +0.0000     |
| real +4% model, both on v1    | +0.0369              | +0.0400     |

> The second row is an unplanned illustration of section 1. Averaged over 200
> draws the +0.0400 improvement recovers correctly at +0.0369, but the standard
> deviation across individual 50-item runs is 0.0239, and 8.5% of individual
> runs measured the real improvement as a *decline*. For a team that runs this
> eval once, the chance of reverting a change that helped is 8.5%.

Asked to compare a v1 baseline against a v2 candidate, the harness returns
INCOMPARABLE: runs cover different items, so nothing can be paired

The two versions do share 50 item ids, and a tempting repair is to restrict
the comparison to those. It does not work here: 5 of the shared ids have
modified content, because reweighting the tier mix changed which topic each
slot draws. An id that survives an edit is not the same item, and `comparable`
is keyed on content hashes rather than ids for exactly that reason.


## 12. Does the gate actually work?

Every section so far has demonstrated a failure mode. This one asks the only
question that matters about the thing built to prevent them: run the gate
against many changes whose true effect is known, and count how often it is
wrong in each direction.

**Predicted.** The gate will ship fewer than 5% of genuinely harmful changes at the default
tolerance, will correctly refuse to claim improvement for null changes at
least 90% of the time, and will label the majority of small true effects
UNDERPOWERED rather than guessing.

**Found.** The gate never claimed an improvement for a genuinely harmful -0.06 change
(0.0%), and claimed one for a null change 1.5% of the time, against a nominal
2.5% for a one-sided false claim at a 95% interval.

The UNDERPOWERED column is the honest part. For small true effects the gate
mostly declines to answer, and says why, rather than reporting the sign of a
coin flip as a finding.

| true effect     | IMPROVED | REGRESSED | INDISTINGUISHABLE | UNDERPOWERED |
|-----------------|----------|-----------|-------------------|--------------|
| harmful (-0.06) | 0.0%     | 97.0%     | 0.0%              | 3.0%         |
| harmful (-0.02) | 0.0%     | 25.0%     | 0.0%              | 75.0%        |
| null (0.00)     | 1.5%     | 6.5%      | 0.0%              | 92.0%        |
| helpful (+0.02) | 23.0%    | 1.0%      | 0.0%              | 76.0%        |
| helpful (+0.06) | 92.0%    | 0.0%      | 0.0%              | 8.0%         |


## 13. Do the intervals cover what they claim to?

A 95% interval that covers the truth 80% of the time is worse than no
interval, because it is believed. Quality scores are bounded and pile up near
the top of the scale, and improvements are usually concentrated in a minority
of items rather than spread evenly -- so the paired differences are skewed,
zero-inflated and truncated, which is where normal theory is least defensible.

The generative process here is deliberately nasty and deliberately realistic:
baseline scores from a Beta(8, 1.5) piled up near 1.0, and an improvement that
touches only 30% of items and is exponentially distributed when it does. That
is what a real prompt fix looks like. It fixes a specific failure mode, so
most items do not move at all.

> The first version of this experiment measured nothing. It defined the truth as
> the mean difference of the same draw the interval was computed from, so every
> procedure covered it by construction and all three columns read 100%. A
> coverage study needs a parameter that is fixed before the sample is drawn; if
> the truth moves with the data, the question is not merely hard to answer, it
> is not a question. The population effect below is therefore estimated once at
> 2,000,000 draws, including the truncation at 1.0, and held fixed.

**Predicted.** On this skewed, truncated, zero-inflated data the z-interval will under-cover
at n=20 by at least two percentage points; the t-interval will recover most
but not all of that; the percentile bootstrap will be no better than the
t-interval at small n; and BCa will be closest to the nominal 95% at every
sample size.

The population effect for this process, including truncation, is 0.01047.
Nominal coverage is 95%.

**Found.** At n=20 the z-interval covers only 85.5% against a nominal 95%, the t-interval
recovers to 86.8%, the percentile bootstrap reaches 86.2% and BCa 91.0%. By
n=200 all four are close to nominal.

The z-interval failure is the one to care about, because `mean +/- 1.96 * sem`
is what gets written when someone reaches for a formula, and at the sample
sizes eval sets run at it is wrong in the direction that makes findings look
more certain than they are.

| n   | z-interval | t-interval | percentile bootstrap | BCa bootstrap | mean BCa width |
|-----|------------|------------|----------------------|---------------|----------------|
| 20  | 85.5%      | 86.8%      | 86.2%                | 91.0%         | 0.0213         |
| 50  | 88.7%      | 89.5%      | 89.7%                | 92.2%         | 0.0139         |
| 200 | 94.7%      | 94.8%      | 95.0%                | 95.8%         | 0.0068         |

BCa coverage across the three sample sizes is 91.0%, 92.2%, 95.8%. The
bootstrap procedures make no distributional assumption, which is why they are
the default in this harness; they are not free, costing 2,000 resamples per
interval, which on a 200-item eval set is a few milliseconds and on no
realistic eval set is the bottleneck.


## 14. Ranking ability and calibration are different questions

A judge with a constant offset ranks perfectly and is wrong about every
absolute value. If the only use is A/B comparison, the offset cancels and the
judge is fine. If anyone reads the absolute score -- and someone always does,
because it goes in a slide -- it is not.

**Predicted.** A judge with a large constant bias will show near-perfect rank correlation and
a large calibration error, and the harness will report it as usable for
ranking but not for absolute scores.

**Found.** At a bias of +0.25 the judge's rank correlation with truth is 0.966 -- it
orders responses essentially perfectly -- while its mean error is +0.2232. The
harness reports usable_for_ranking=True and usable_for_absolute_scores=False.

Collapsing these into one 'judge quality' number would have to choose which of
two true statements to discard.

| judge bias | Pearson | Spearman | mean error | mean abs error | ranking | absolute |
|------------|---------|----------|------------|----------------|---------|----------|
| +0.00      | 0.980   | 0.979    | 0.0051     | 0.0370         | yes     | yes      |
| +0.05      | 0.977   | 0.976    | 0.0510     | 0.0605         | yes     | yes      |
| +0.15      | 0.974   | 0.979    | 0.1321     | 0.1321         | yes     | no       |
| +0.25      | 0.941   | 0.966    | 0.2232     | 0.2232         | yes     | no       |


---

17 predictions were written before the corresponding measurement was read. 14 held; 3 did not, and each of those is discussed where it appears.

Generated by `run_eval.py` on Python 3.12.10 with numpy 2.5.2. Every random draw is seeded, so regenerating this file produces identical bytes; `tests/test_results_integrity.py` asserts it and pins the hash.
