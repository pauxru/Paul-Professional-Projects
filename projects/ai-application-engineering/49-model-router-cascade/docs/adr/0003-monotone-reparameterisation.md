# ADR 0003 — Recalibration is a reparameterisation of the threshold, not an improvement

**Status:** accepted
**Date:** 2024-06

## Context

Section 1 of the experiment measures the small model's confidence signal and
finds the textbook pathology: **ECE 0.059, MCE 0.226** — the worst-calibrated
bin claims 0.145 and delivers 0.371 — while **AUC is 0.918**, meaning the signal
ranks queries extremely well.

The standard advice is temperature scaling: fit a single scalar on held-out data,
divide the log-odds by it, and the cascade improves.

I implemented it, and it worked as calibration: fitted temperature **1.59**
against a simulator truth of 1.90 (recovering the generative parameter is itself
a test; the 0.31 shortfall is the lossy confidence clamp, documented in
known-limitations §2), **ECE fell 51%** to 0.029, MCE fell to 0.147, Brier
improved.

Then the frontier did not move. Not approximately — at all.

## The result

```
confidence signal          ECE       MCE     Brier       AUC
raw                     0.0588    0.2263    0.1198    0.9177
temperature-scaled      0.0288    0.1467    0.1151    0.9177
```

AUC is *identical to four decimal places* because temperature scaling is
strictly monotone and AUC depends only on the ranking.

The first frontier check I wrote compared the raw sweep grid to the scaled sweep
grid point by point and reported `false` — the two sweeps land on different
thresholds, so of course they differ. **That check was measuring the wrong
thing**, and it would have let me publish "recalibration changes the frontier"
with a straight face.

The right check pairs each scaled threshold `T` with the raw threshold it is
*supposed* to equal, and compares the two policies query by query:

```
48 threshold pairs checked
48 agreed on every one of the 4,000 queries
largest disagreement across all pairs: 0 queries
```

## Decision

**Record as a first-class finding that a monotone recalibration cannot change a
threshold cascade's decision set, and pin it with a per-query test.**

The argument is elementary once stated. A threshold cascade evaluates exactly one
predicate: `confidence >= T`. Temperature scaling is a strictly increasing
function `f`. For any strictly increasing `f`, `f(c) >= T ⟺ c >= f⁻¹(T)`. So the
scaled cascade at threshold `T` and the raw cascade at threshold `f⁻¹(T)` select
the *identical subset* of queries. Same escalations, same accuracy, same cost,
same latency. The frontier is the same set of points; only the labels on the dial
change.

The inverse is available in closed form, which is what makes this testable rather
than merely arguable. With

```
temper(p, t) = p^(1/t) / ( p^(1/t) + (1-p)^(1/t) )
```

we have `logit(temper(p,t)) = logit(p)/t`, so `temper(·, 1/t)` is exactly
`temper(·, t)⁻¹`.

`TestRecalibrationIsAReparameterisation` therefore asserts, for four temperatures
× 19 thresholds × 2,000 held-out queries, that `Cascade{Temperature: t, Threshold: T}`
and `Cascade{Threshold: Temper(T, 1/t)}` produce identical `Escalated`, `Correct`,
`CostCents` and call counts. Not statistically similar — identical.

`TestValueCascadeIsNotInvariantUnderRecalibration` asserts the converse for the
value rule, so the boundary of the claim is pinned from both sides.

## What recalibration is still for

Two things, and the report says both.

1. **The knob becomes interpretable.** On the raw signal, "escalate below 0.90"
   escalates 78% of traffic. On the scaled signal it escalates 88%, and those
   really are the queries the small model gets right less than 90% of the time.
   The threshold can be set from a product requirement rather than tuned against
   a spreadsheet. That is genuinely valuable and it is an *operability* win.
2. **Rules that do arithmetic on the confidence are affected.** The value cascade
   computes `(P_large − confidence) / marginal_cost`, which is not
   monotone-invariant. Measured: +0.7 points at its best λ, +0.08 averaged over
   16 λ, winning at 7 of 16 — real but inside the noise, and reported that way
   rather than as the best point.

## Consequences

- The generalisable question is **"does anything downstream do arithmetic on the
  probability?"** If the answer is no, calibration cannot change behaviour and a
  calibration project cannot pay for itself in quality. Whether calibration
  matters is a property of the *consumer* of the signal, not of the signal.
- The second-order question is **where the distortion sits relative to the
  decision boundary.** Section 1's histogram shows the error concentrated at the
  extremes, where 66% of the mass sits and the decision is unambiguous either
  way. Fixing a number that was never close to the line changes nothing. Both
  questions are answerable in an afternoon and neither is the one that gets
  asked.
- ECE and MCE are both reported, always. The mean-confidence-minus-accuracy
  summary showed a gap of only −2.3 points, because the signal is too extreme at
  *both* ends and the errors cancel. That summary was actively misleading and the
  report now says so in place of quoting it.
