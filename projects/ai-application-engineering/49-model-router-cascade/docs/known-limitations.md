# Known limitations

Written to be read before the results, not after.

## 1. No language model is called

`internal/models` is a deterministic simulator. This is the largest limitation
and everything below follows from it.

**What that invalidates.** Every absolute number in `docs/results.md` — 86.2%
accuracy, 3,732¢, ECE 0.0588 — is a property of the simulator's parameters. They
are not measurements of any real model and must not be quoted as such.

**What it does not invalidate.** The structural results are properties of the
*algorithms*, and they hold for any fleet with three qualitative properties:

| Assumption | Where it enters | How wrong could it be? |
|---|---|---|
| Bigger models cost more and are more likely to be right | `Model.Accuracy` is logistic in difficulty; cost is linear in tokens | Robust. This is the premise of having a fleet at all. |
| Confidence ranks better than it calibrates | `distort()` multiplies log-odds by a per-model temperature | Robust in direction, uncertain in size. Real models' miscalibration is not a clean single-parameter distortion. |
| Prompt size varies far more than difficulty does | `ContextTokens`, lognormal, independent of difficulty | Workload-dependent. See §3. |

The three headline findings — the hull is the right baseline, a monotone
recalibration cannot move a threshold's frontier, dividing by marginal cost
beats a fixed threshold — are consequences of those assumptions plus arithmetic.
The second one is not even statistical: it is a theorem about monotone functions,
and the experiment only confirms the implementation matches it.

## 2. The confidence clamp is lossy, and it is why the fitted temperature undershoots

`distort` clamps reported confidence to `[0.001, 0.999]` so no simulated model
ever claims absolute certainty. That clamp is **not invertible**, so temperature
scaling cannot undo the distortion in the tails.

This has a visible consequence in section 4: the fitted temperature is **1.59**
against a simulator truth of **1.90**. That is not a fitting failure. The clamp
compresses the most extreme reports inward, so the observed signal genuinely *is*
less distorted than the generative parameter says, and the fit correctly follows
the evidence rather than the parameter.

`TestDistortAndTemperAreExactInverses` asserts the round trip only where the
clamp does not bite, and `TestTheClampBreaksInvertibilityInTheTails` pins the
loss deliberately, with `TestFittedTemperatureRecoversTheModelsCalibration`
asserting the bias is **downwards**. Pinned rather than removed: a production
wrapper around a real model clamps too.

## 3. A single fleet, a single seed for the headline numbers

The report is generated at seed 20240612 with one three-model fleet. Structural
claims are checked across sweeps (48 threshold pairs, 16 λ values, 4
temperatures in the test suite) rather than at single points, but no result here
is averaged over multiple *fleets*. A fleet whose mid-tier model sits *below* the
small→large chord would change section 3's conclusions about the three-stage
cascade — the report checks hull membership before using it, but only for this
fleet.

## 4. The workload's cost/difficulty independence is an assumption, not a finding

Section 5's entire argument rests on prompt size being poorly correlated with
difficulty (+0.31 here). I built the workload that way deliberately, and I have
made the reasoning explicit rather than hiding it in a generator: real streams
contain pasted logs, contracts and threads whose size is set by the user's
copy-paste habit, not by the question.

**If your workload does not have that property, the value rule's advantage
shrinks toward zero.** My first version of this workload had a 12x spread and a
strong correlation, and the value rule showed almost no gain — that is recorded
in ADR 0004 because it is the honest boundary of the claim. Measure
`corr(log tokens, difficulty)` on your own traffic before assuming section 5
transfers.

## 5. Confidence is assumed to exist and to be free

The cascade needs a per-answer confidence signal. Real providers mostly do not
expose one. In practice you get it from token logprobs (unavailable on several
major APIs), a self-critique call (which costs another call and changes the
economics substantially), or a trained verifier (which costs training data).

If obtaining confidence costs a second call, the cascade's economics change
qualitatively and the classifier — which decides *before* spending anything —
becomes relatively more attractive than section 3 makes it look.

## 6. Accuracy is binary

Every answer is right or wrong. Real quality is graded: partially correct,
correct but verbose, correct but unsourced. A binary metric hides the case where
the large model's answer is *better* rather than *correct where the small one was
wrong*, which is where much of the real value of escalation lives.

## 7. The classifier is deliberately weak

Logistic regression on five surface features, batch gradient descent, no
regularisation tuning, no cross-validation. It reaches held-out AUC 0.825 against
the confidence signal's 0.918.

A stronger classifier — gradient boosting, or an embedding of the prompt — would
narrow that gap and might close it. **Section 3's conclusion is "one real attempt
beats every *surface* feature", not "no classifier can beat a cascade".** The
latter is a stronger claim and this project does not support it.

## 8. Latency is modelled, not measured

Lognormal jitter around a per-model median. Real p99 latency is driven by
queueing, rate limits, retries and cold starts, none of which are here. The p95
figures are internally consistent and should not be read as capacity planning.

## 9. `P(large is right)` is estimated once and never updated

The value rule needs the large model's accuracy. It is fitted on the training
split and held fixed. In production this drifts with the workload mix and with
model updates, which biases the meaning of λ. The rule degrades gracefully — a
stale estimate shifts the operating point rather than breaking the rule — but it
does drift, and a production version needs a rolling estimate and an alert on it.

## 10. Section 6's provider failures are injected on a schedule

The degradation that trips the circuit breaker is a scripted failure window, not
a model of real provider incidents. It is sufficient to exercise the state
machine and the false-dawn path — which is what the tests assert — and it is not
evidence about how often real providers fail or for how long.

## 11. The three-stage cascade is under-explored

`Cascade3` sweeps a coarse grid of two thresholds. There are better joint
operating points than the one reported, and the reported figure (+5.4 against the
hull) is a lower bound on what the family can do. It was left coarse because the
two-stage results carry the argument and a finer sweep would not change any
conclusion.
