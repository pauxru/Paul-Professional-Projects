# The bugs the experiment found

Seven defects, six in library code that already had passing tests and one in
the code that verifies it. None was found by reading the code. Every one was
found because a measurement came out wrong and the wrongness was small enough
to be plausible.

That last clause is the point of this document. A crash gets fixed in
minutes. A number that is off by a factor of two, in a table of forty numbers,
in a report you wrote yourself, is a different problem entirely.

---

## 1. The judge whose noise was free

**Symptom.** Increasing the simulated judge's noise from 0.001 to 0.20 had no
effect whatsoever on the width of the paired confidence interval. The
intervals were identical to the last digit.

**Cause.** The judge seeded its noise from the item id. In a paired comparison
both systems are scored on the same items, so both received the *identical*
perturbation, and it subtracted out exactly in the difference.

**Why it mattered more than it looked.** The report would have concluded that
judge quality does not affect A/B decisions. That conclusion is false,
actionable, and would have been delivered with confidence intervals around it.
It is also superficially plausible -- paired designs *do* cancel systematic
judge bias, which is a real and useful property. The error was extending that
to all judge error.

**Fix.** Split the judge's error into a stable component keyed on the item and
a fresh component keyed on the item *and the response text*. Two systems
produce different text for the same item, so the fresh component survives
pairing. Exposed as `consistency`. Full reasoning in ADR 003.

**What would have caught it earlier.** A test asserting the statistical
property rather than the implementation. `test_judge_noise_does_not_cancel_
completely_in_paired_differences` asserts that paired spread is monotone in
judge noise -- it reads no seeds and no keys. Note that a test asserting "the
judge is deterministic for a given item and response" passes on the buggy
version, and so does "noise makes scores vary." The defect lived entirely in
the *relationship between two runs*, which is invisible to any test that looks
at one run.

---

## 2. The two datasets that were the same dataset

**Symptom.** The winner's-curse experiment reported that **0.0%** of a sweep
winner's apparent gain evaporated on held-out data. The expected answer was
"most of it." Zero point zero is not a plausible measurement of anything.

**Cause.** `make_dataset` built item ids from the tier and index only --
`easy-000`, `hard-014`. The dataset *name* was not in the id. Two datasets
built with different names were byte-identical in every field the simulation
looks at, so the "held-out" set was the training set.

**Why it mattered.** Silently, the experiment measured nothing. The mechanism
under test -- selection bias -- requires two genuinely independent samples,
and there was one sample used twice. Every number in the section was internally
consistent and meaningless.

**Fix.** Ids are `f"{name}:{tier[:3]}-{j:03d}"`. The measurement flipped from
0.0% to 107.1%.

**The class.** An identifier that is unique within its intended scope, used
across scopes. `easy-000` is a perfectly good id *within* a dataset. It is not
a good id *across* datasets, and nothing in the type system distinguishes
those two uses.

---

## 3. The guard that could not fire

**Symptom.** `paired_bca_bootstrap` occasionally produced an interval with an
infinite bound, on inputs that were not degenerate.

**Cause.** BCa estimates a bias-correction `z0` as the fraction of bootstrap
replicates below the observed statistic. There was a guard for the degenerate
case where all replicates are equal. It read, in effect, `if boot.max() ==
boot.min()`.

With float data that condition is essentially never true. Bootstrapping a
constant offset gives replicates that differ in the last two or three bits --
a spread of 1e-17, not zero. So the guard did not fire, `z0` was estimated
from rounding noise, and the BCa transformation

```
alpha_adjusted = Phi(z0 + (z0 + z) / (1 - a*(z0 + z)))
```

was evaluated with a denominator that can pass through zero.

**Fix.** Two changes. Guard on the *scale* of the bootstrap spread relative to
the data rather than on exact equality, and add an explicit check for the pole,
falling back to the percentile bootstrap in both cases.

**The class, which is new in this catalogue.** A guard whose condition is
unreachable in the numeric regime it was written for. It is worse than no
guard: it looks like the case was handled, so nobody checks. Every reviewer of
that function -- including me, several times -- read `if max == min` as "handle
the constant case" and moved on.

---

## 4. The judge that said yes to everything and was rated trustworthy

**Symptom.** A test written to be obviously true -- an always-yes judge on
95%-pass data should not be `trustworthy` -- failed.

**Cause.** `trustworthy` was built on Cohen's kappa, with a fallback to Gwet's
AC1 when the prevalence index indicated kappa was being suppressed by skew.
That is the textbook recommendation and it is right about the problem it
addresses. AC1 is constructed so that prevalence does not collapse its chance
term.

The same construction means **degeneracy does not collapse it either**. A
constant labeller scores AC1 above 0.94 on 95%-pass data.

**Fix.** Add the comparison neither statistic makes: does the judge beat the
best constant labeller? Both kappa and AC1 answer "how much better than random
guessing is this?" The question that decides whether to deploy a judge is "how
much better than *not having one*?"

**Why this is the most interesting bug here.** It is not an implementation
error. The code correctly computed two statistics that the literature
recommends, and the statistics correctly answered the question they are
designed to answer. The defect was in the assumption that their question was
the same as the deployment question. No amount of testing the implementation
would have found it, because the implementation was right.

---

## 5. The fix that repeated the bug it fixed

**Symptom.** None. Everything passed. The extended section 7 table showed a
judge at 92% prevalence with a margin of +0.0040 over the constant labeller,
flagged `informative = yes` and `trustworthy = yes`, alongside a bootstrap
interval of [-0.0073, +0.0145].

**Cause.** The fix for bug 4 defined `informative` as `observed >
baseline_agreement` -- a comparison of two point estimates, with a winner
declared on any positive difference, on 4,000 items.

That is the exact error this entire repository exists to document. It was
sitting inside the property written to detect that error.

**Fix.** Require the margin to be statistically distinguishable from zero.

**And then the useful part.** Working out how to test the margin properly
meant writing it down algebraically. With the confusion matrix as
`(a, b, c, d)` for (both pass, judge-only pass, human-only pass, both fail),
the judge is right on `a + d` and the constant labeller -- on pass-majority
data -- is right on `a + c`. So

```
margin = (a + d)/n - (a + c)/n = (d - c)/n
```

and `a` does not appear. **Every item where the human passed and the judge
agreed contributes exactly nothing to the judge's case**, because a hard-coded
string got that item too. On a 92%-pass dataset that is 92% of the items, and
they are the items that produce the agreement percentage in the slide deck.

Once written that way it is a count of discordant pairs, so under the null each
discordant item is a fair coin and the exact distribution is binomial. No
resampling is needed. The bootstrap and the exact test agree everywhere in the
section 7 table, and the bootstrap costs 4,000 resamples per row to reproduce
a closed form.

The bootstrap is still in the code. It is the evidence that the two agree.

---

## 6. The penalty that was a bonus

**Symptom.** A test asserting that hard items score lower than easy ones, given
`tier_penalty={"hard": 0.25}`, failed with hard at 0.915 and easy at 0.747.

**Cause.** The field was applied as `base_quality + tier_penalty[tier]`. It
held signed offsets -- the default was `{"easy": 0.12, "hard": -0.18}` -- and
the arithmetic was correct for every caller in the repository.

Every caller had been written by the person who wrote the field, who never had
to read the name to know what it meant. The first caller written by someone
reading the name passed a positive "penalty" and got a model that was better on
those items.

**Fix.** Renamed to `tier_offset`, with a docstring that says it is signed and
records why.

**Why it is in this list at all,** given that no shipped behaviour was wrong:
the assertion failed loudly only because it was checking the thing directly. A
test that used the fixture incidentally -- setting up a "model that struggles
on hard items" as scaffolding for a *different* assertion -- would have passed
with the fixture inverted underneath it, and the resulting section of the
report would have argued something false about slice analysis.

---

## 7. The mutation that did not mutate anything

This one is a defect in the verification apparatus rather than in the library,
which is why it is last, and it is the one I would least have predicted.

`test.ps1` stage 3 runs a small mutation suite: it edits a line of source,
re-runs the tests, and requires them to fail. The point is to check that the
regression tests for bugs 1 and 5 actually constrain the code rather than
merely coexisting with it. Three mutations, three expected kills.

One survived: *"judge noise cancels in paired differences again (bug 1)."*

The obvious reading of a surviving mutation is that the test suite is weak.
That reading was wrong. The judge draws two noise components:

```python
stable_rng = default_rng(_item_seed(response.item_id,          f"judge-item:..."))
fresh_rng  = default_rng(_item_seed(f"{response.item_id}|{response.text}",
                                                               f"judge-fresh:..."))
```

Bug 1 was that the fresh component was keyed on the item alone, so two systems
scored on the same item received the *identical* perturbation and it cancelled
exactly in the paired difference. The fix was to key it on the response text as
well. My mutation changed `judge-fresh:` to `judge-item:` -- the **salt**. The
salt only selects a different stream. The **key** still contained
`response.text`, so the two systems still drew different values and the noise
still did not cancel. The mutation changed which random numbers came out and
changed nothing about the property under test. The correct mutation replaces
the key:

```powershell
From = 'f"{response.item_id}|{response.text}"'
To   = 'response.item_id'
```

With that, `test_judge_noise_does_not_cancel_completely_in_paired_differences`
fails, as it always would have. The test was fine. The test of the test was not.

What makes this worth recording is the near miss. The mutation suite has three
entries and two of them killed. Had this one been killed too -- and a mutation
that reshuffles a random stream can easily perturb a threshold enough to break
*some* unrelated assertion -- I would have written down "bug 1 is covered by a
mutation test" and been wrong, with a green stage 3 as the evidence. A
surviving mutation is loud. A mutation that passes for the wrong reason is
silent, and it manufactures confidence rather than merely failing to provide
it.

That puts it in the same family as bugs 3 and 5: **it looks handled.** The
difference is that bugs 3 and 5 were guards inside the library, and this was a
guard around the library. Verification code is code. It is not exempt from the
failure modes it exists to detect, and nothing in this repository was checking
it -- the mutation suite is the last link in the chain, and the last link has
nothing behind it.

---

## What the seven have in common

Three of the seven -- 1, 2, and 6 -- are the same shape, and it is the shape
that dominated the twelve-bug catalogue from the previous project in this
series: **a value that is correct for the consumer the author had in mind, used
by a consumer the author had not.** A seed key correct within a run, used across
runs. An id unique within a dataset, used across datasets. An offset whose
sign convention was correct for callers who already knew it, read by a caller
who did not.

Bugs 3, 5 and 7 are a class that project did not have. Bug 3 is a guard whose
condition is unreachable in its own numeric regime. Bug 5 is a check that
commits the error it was written to catch. Bug 7 is a check that does not
exercise the thing it names. All three share a property that makes them
unusually durable: **they look handled.** A reviewer sees `if max == min`
and reads "degenerate case: covered." A reviewer sees `informative` and reads
"baseline comparison: covered." A reviewer sees a mutation labelled `bug 1` and
reads "regression: covered." The presence of the guard is what stops anyone
from checking whether the guard works.

Bug 4 is on its own and is the one I would most want to have found in a
production system rather than a simulation. Nothing was implemented
incorrectly. Two well-chosen statistics correctly answered the question they
are designed for. The failure was entirely in the gap between that question
and the decision being made, and the only thing that surfaced it was writing
down, in a test, a case where the right answer was obvious.

## The method, stated plainly

Every one of these was found by a measurement, not by reading code. Four were
found because a number was *implausible* rather than impossible -- 0.0%, an
interval that did not move, an AC1 of 0.94 on a constant, a margin of +0.004
called significant.

The discipline that produced them is the one enforced by `report.py`: **write
the prediction down before looking at the number.** Without a written
expectation, 0.0% is just the value in the cell. With one, it is a
contradiction that has to be explained, and explaining it is what finds the
bug. Three of the seventeen predictions in the report were genuinely wrong and
are marked as such; the machinery that makes those three visible is the same
machinery that made these six findable.
