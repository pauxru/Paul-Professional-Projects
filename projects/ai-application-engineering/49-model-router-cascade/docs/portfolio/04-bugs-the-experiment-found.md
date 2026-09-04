# 4 — Four bugs the experiment found in my own design

I wrote the packages, wrote the tests, got green, and ran the experiment. Then I
read the output.

Four things were wrong. Not one of them was a crash, none were caught by the test
suite as it stood, and every one would have produced a confident, plausible,
well-formatted report making a claim that was false.

This is the argument for building the *experiment* rather than only the *library*.
Unit tests check that the code does what you told it to. Running the whole thing
and reading the numbers checks whether what you told it to do was the right thing.

---

## Bug 1 — the sign was inverted on the thing the project is about

`models.distort` applies the confidence miscalibration. I wrote:

```go
return math.Pow(p, 1/t) / (math.Pow(p, 1/t) + math.Pow(1-p, 1/t))
```

Which **divides** the log-odds by `t`, pulling probabilities toward 0.5. So
setting the small model's `Calibration: 1.9` — documented as "overconfident" —
made it *under*confident. The report's central narrative, that small models
overstate their confidence, was contradicted by the simulator generating the data
for it.

The symptom was not an error. It was section 1's reliability table sloping the
wrong way, which I only noticed because I had written a sentence above it
predicting the slope.

Fixed by multiplying instead:

```go
return math.Pow(p, t) / (math.Pow(p, t) + math.Pow(1-p, t))
```

The fix came with a test that would have caught it:
`calibration.Temper(distort(p, t), t) == p`, and — better —
`TestFittedTemperatureRecoversTheModelsCalibration`, which asserts that fitting a
temperature from the simulator's own output recovers the `Calibration` field
that produced it. The round trip closes. Before the fix it could not have.

**And the round-trip test then found something else.** It failed at `p = 0.05,
t = 3.0`, because `distort` clamps its output to `[0.001, 0.999]` and a clamp is
not invertible. That is why section 4's fitted temperature is 1.59 against a
simulator truth of 1.90 — the clamp compresses the most extreme reports inward,
so the signal genuinely *is* less distorted than the parameter says, and the fit
follows the evidence. I had been about to write that 0.31 off as fitting error.
It is now `TestTheClampBreaksInvertibilityInTheTails`, plus an assertion that the
bias goes *downwards*, plus a paragraph in known-limitations.

**Lesson:** when two components implement inverse operations, assert the round
trip. A sign error in a symmetric-looking formula is invisible on inspection and
obvious under `f(g(x)) == x` — and the round trip will also find every place your
implementation is not actually a bijection, which is where the interesting
behaviour usually is.

---

## Bug 2 — my baseline was too weak, and it flattered every result

Covered at length in ADR 0001 and write-up 1, so briefly: I used random
escalation between the cheapest and dearest model as the zero-information
baseline. It is the standard choice and it traces the chord between the
endpoints, which I verified empirically.

Then I printed the mid-tier model's fixed-policy row and it sat **19.3 points
above that chord**.

Every "vs baseline" number in the report was inflated. The flagship cascade went
from +6.4 points to +2.5 once scored against the convex hull. The learned
classifier went from looking like a modest success to **+0.3 points**, which is
nothing.

**Lesson:** the baseline decides the verdict more often than the algorithm does.
Enumerate *all* the trivial policies, not two, and take their upper convex hull —
random mixing attains the whole hull for free, so anything below it is beaten by
a coin flip.

I kept the wrong baseline in the codebase (`frontier.Line`) and print both
side by side in the report, because the error is more instructive when visible.

---

## Bug 3 — the workload had assumed away the question

My generator computed tokens from word count and word count from difficulty. Two
consequences I did not see until I printed the diagnostics:

```
token spread:                       12x
corr(log tokens, difficulty):     strong
```

In that workload, expensive queries *are* the hard ones, so a fixed confidence
threshold is nearly optimal by accident, and the cost-aware value rule — the idea
I most wanted to test — showed almost no gain. I was one commit away from
concluding the idea did not work.

The generator was wrong. Real request streams have a large component of prompt
size driven by whatever the user pasted, which is unrelated to difficulty. Adding
`ContextTokens` (lognormal, 38% of queries, independent of difficulty) gave:

```
token spread:                      104x
corr(log tokens, difficulty):    +0.310
```

and the value rule became the largest result in the report: **+3.9 points and 25
percentage points of the bill**.

**Lesson:** an experiment can only find effects its inputs are capable of
exhibiting. Print the diagnostics of your *generator* — spread, correlation,
class balance — and check them against what you believe about the real world,
before you interpret a single result. A negative result on a workload that
assumed away the mechanism is not a negative result.

---

## Bug 4 — the check for the headline claim was checking the wrong thing

Section 4's claim is that recalibration does not move a threshold cascade's
frontier. My first check swept thresholds on the raw signal, swept the same grid
on the scaled signal, compared the two Pareto frontiers, and printed `false`.

**The check was meaningless.** A threshold means a different thing on each signal,
so the same numeric grid samples different operating points on each. Comparing
them establishes only that 0.5 is not 0.5 after a rescaling.

And it printed `false`, which is the dangerous part — I had a check, it ran, it
gave an answer, and the answer was the opposite of the truth. A missing check
gets noticed. A wrong check gets cited.

The correct check pairs each scaled threshold `T` with the raw threshold
`temper(T, 1/t)` it is *supposed* to equal, and compares the two policies **per
query**:

```
48 threshold pairs checked
48 agreed on every one of the 4,000 queries
largest disagreement across all pairs: 0
```

**Lesson:** when the claim is "these two things are the same", the check has to
be a *pairing*, not a comparison of two independently-generated summaries.
Aggregate comparisons of separately-swept grids are a common shape of this
mistake, and they fail in the direction that makes you look right.

There is a fifth, smaller instance of the same class. `Report.Diagram()`
originally summarised calibration as mean confidence minus accuracy, giving −2.3
points and the word "slightly". The distortion is worst at *both* extremes, so
the errors cancel and the summary was near-useless. It now prints MCE (0.226)
and explains why the aggregate is misleading. Same failure mode: a number that
was computed correctly and answered the wrong question.

---

## What the four have in common

None of them were caught by tests, because **the tests encoded the same
misunderstanding as the code**. `distort` was tested to be a monotone map into
(0,1) — which it was, in the wrong direction. The baseline was tested to
interpolate correctly between the two policies it was given — which it did, from
the wrong pair. The workload was tested to produce well-formed queries with the
right class shares — which it did, with the wrong correlation structure. The
frontier check was tested to compare two frontiers — which it did, the wrong two.

The thing that caught all four was **printing an intermediate quantity next to a
sentence predicting what it should be**, then reading it. Section 0 of the report
exists for exactly that reason: it prints the fleet, the class mix, the token
percentiles and the cost/difficulty correlation before any result, and each is
followed by a claim about what it should look like and why it matters. Three of
these four bugs were found in that section.

That is a cheap habit and it is the highest-yield one I know:

> Print the inputs. Write down what you expect them to say. Then look.
