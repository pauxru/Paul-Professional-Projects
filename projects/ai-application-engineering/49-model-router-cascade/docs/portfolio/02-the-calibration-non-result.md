# 2 — The calibration result that says do nothing

This is the part of the project I would put in front of a hiring manager, because
the finding is that a well-known, widely-recommended, technically-correct
improvement does *precisely nothing* — and the reason is a two-line argument that
nobody makes.

## The setup

A cascade escalates when the cheap model is unsure. So before building one,
measure whether "unsure" means anything. Two separate questions, and they have
separate answers:

- **Does the signal rank correctly?** → AUC
- **Are its numbers true?** → ECE

Here is the small model's confidence on 4,000 held-out queries:

```
confidence            n    claimed   observed       gap
[0.00, 0.10)       1752      0.011      0.065    +0.054
[0.10, 0.20)        175      0.145      0.371    +0.226
[0.20, 0.30)        136      0.251      0.397    +0.146
...
[0.80, 0.90)        293      0.855      0.809    -0.046
[0.90, 1.00)        898      0.967      0.920    -0.048

ECE 0.0588   MCE 0.2263   Brier 0.1198   AUC 0.9177
```

The ranking is excellent (AUC 0.918). The numbers are badly wrong — the worst bin
claims 0.145 and delivers 0.371.

Note the trap in that table. Mean claimed confidence is 0.407; actual accuracy is
0.430. A **2.3-point** aggregate gap, which looks nearly harmless. It is not: the
signal is too *extreme at both ends*, so the low-bin and high-bin errors cancel
when you average them. My first version of the report printed exactly that
summary and called the model "slightly overconfident". That sentence was
worthless. MCE — the worst bin, 0.226 — is what catches it, and the report now
prints both and says why.

## The recommended fix, applied properly

Temperature scaling. Fit one scalar `t` on held-out data, divide the log-odds by
it. Fitted on the **training** split only, never the eval split.

It worked, as calibration:

```
                      ECE       MCE     Brier       AUC
raw                0.0588    0.2263    0.1198    0.9177
scaled             0.0288    0.1467    0.1151    0.9177
```

Fitted temperature **1.59** against a simulator truth of 1.90 — the fit recovers
the generative parameter, so the machinery demonstrably works. **ECE fell 51%.**
Brier improved.

(The 0.31 shortfall is not fitting error. `distort` clamps reported confidence to
`[0.001, 0.999]`, and that clamp is not invertible — it compresses the most
extreme reports inward, so the observed signal genuinely *is* less distorted than
1.90, and the fit correctly follows the evidence rather than the parameter.
`TestTheClampBreaksInvertibilityInTheTails` pins that, and
`TestFittedTemperatureRecoversTheModelsCalibration` asserts the bias is
downwards. I found this only because the round-trip test I wrote to catch a
different bug failed at p=0.05.)

AUC moved from 0.9177 to 0.9177.

## The check I got wrong first

I swept thresholds on the raw signal, swept thresholds on the scaled signal,
compared the two Pareto frontiers point by point, and printed `false` — they
differ.

**That check was measuring nothing.** The two sweeps use the same threshold
*grid*, but a threshold means a different thing on each signal, so of course the
grids do not line up. Comparing them proves only that 0.5 on one scale is not 0.5
on the other.

Had I stopped there I would have reported "recalibration changes the frontier",
which is false, and I would have had a plausible-looking table to back it up.

## The right check

Pair each scaled threshold `T` with the raw threshold it is *supposed to equal*,
and compare the two policies **query by query**:

```
48 threshold pairs checked
48 agreed on every one of the 4,000 queries
largest disagreement across all pairs: 0 queries
```

Zero. Not small — zero.

## Why it has to be zero

A threshold cascade evaluates exactly one predicate:

```
confidence >= T
```

Temperature scaling is a strictly increasing function `f`. For any strictly
increasing `f`:

```
f(c) >= T   ⟺   c >= f⁻¹(T)
```

So the scaled cascade at threshold `T` and the raw cascade at threshold `f⁻¹(T)`
select the **identical subset** of queries. Same escalations, same accuracy, same
cost, same latency. The frontier is the same set of points. Only the numbers
printed on the dial changed.

The inverse is closed-form, which is what makes this testable rather than merely
arguable. With

```
temper(p, t) = p^(1/t) / ( p^(1/t) + (1-p)^(1/t) )
```

we get `logit(temper(p,t)) = logit(p)/t`, so `temper(·, 1/t)` is exactly the
inverse. The test asserts it across four temperatures × 19 thresholds × 2,000
queries.

## So is calibration useless?

No — and this is where the finding becomes actionable rather than merely clever.

**Calibration makes the knob mean something.** On the raw signal, "escalate below
0.90" escalates 78% of traffic. On the scaled signal it escalates 88%, and those
really are the queries the small model gets right less than 90% of the time. The
threshold can be set from a product requirement — "escalate anything we'd get
right less than 9 times in 10" — instead of being tuned against a spreadsheet.
That is a real operability win and it is worth doing.

**And rules that do arithmetic on the confidence are genuinely affected.** The
value cascade computes `(P_large − confidence) / marginal_cost`. Subtraction is
not monotone-invariant. Measured: +0.7 points at its best λ — but **+0.08
averaged over 16 λ values, winning at 7 of 16**. Real, and inside the noise of
choosing λ. The report says that rather than quoting the best point, because
best-point comparisons reward whichever variant got luckier on the grid.

## The general form

> **Whether calibration matters is a property of how the decision rule *consumes*
> the signal, not a property of the signal.**

Two questions, both answerable in an afternoon, neither of which is the one that
gets asked:

1. **Does anything downstream do arithmetic on the probability, or does it only
   compare it to a constant?** If only compared, a monotone recalibration cannot
   change behaviour. Full stop. No experiment needed.
2. **If it does arithmetic — are the queries near the decision boundary the
   distorted ones?** Here they are not. Section 1's histogram shows the error
   concentrated at the extremes, where 66% of the mass sits and the decision is
   unambiguous either way. Fixing a number that was never close to the line
   changes nothing.

That is how a team spends a quarter on a calibration pipeline, hits every metric
in the plan, and ships a product that behaves identically. Every number in the
project plan improves. The thing the plan existed to improve does not move.
