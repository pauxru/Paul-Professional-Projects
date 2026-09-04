# ADR 004 — One calibration rule for every threshold, and a different one for the CUSUM

**Status:** accepted

## Context

Eight detectors are compared on six scenarios. Each produces a score per day, and each
needs a threshold. If the thresholds are chosen by hand, or tuned per detector until the
chart looks right, the comparison measures the tuning rather than the detector — and the
tuning was done by someone who already knew which scenario was which.

## Decision

Every threshold is the `1 − ALPHA` quantile of *that detector's own scores over the
reference window*, computed by the same three lines of code for all eight. No detector gets
a hand-picked constant. `ALPHA` is one number, declared once.

The CUSUM is the exception, and it is an exception on purpose. Its threshold comes from
`bootstrap_cusum_threshold`, a Monte-Carlo simulation of the monitoring horizon.

## Consequences

The uniform rule makes the panel comparable. It also makes the results *worse-looking* than
they would be under tuning — the quality canary would detect `template-regression` at a
lower alpha, and the three input-shift-sensitive detectors would stop false-alarming at a
higher one — and that is the point. A comparison you tuned is a comparison you decided.

The CUSUM needed its own rule because the uniform rule is *wrong* for it, not merely
suboptimal, and understanding why took three attempts.

**Attempt 1 — the uniform rule.** Take the 99th percentile of the CUSUM statistic over the
30 reference days. It false-alarmed in all six scenarios, including the healthy one. A
per-day statistic and a cumulative one are not the same kind of object: the quantile of a
random walk's first 30 steps says nothing useful about its maximum over the next 60, because
a reflected random walk with zero drift crosses **any** fixed threshold eventually. The
uniform rule quietly asks "how high does this go in 30 days?" and then applies the answer to
a longer window.

**Attempt 2 — bootstrap the horizon.** Simulate 60 days of reference-distributed data,
run the same CUSUM, record the maximum, and take the `1 − ALPHA` quantile across trials.
This is the textbook answer and it is a large improvement: false alarms dropped from nine
days to one. But one is not zero, and the residual was not noise.

**Attempt 3 — bootstrap the reference window too.** The live detector does not know the
true mean. It *estimates* `target` from 30 days and applies that estimate to 60 fresh ones.
If the sample mean happens to land a standard error low, then every subsequent day is above
target on average, and the CUSUM — which integrates — turns that small bias into a linear
ramp that crosses any horizon-calibrated threshold. The bootstrap in attempt 2 used the
*true* mean and therefore simulated an easier problem than the one being solved.

The fix is one line of intent: each trial resamples the reference window **and** the
horizon, estimates `target` from its own resampled reference, and runs the CUSUM on its own
resampled horizon. The simulated statistic now carries the same estimation error as the
live one. False alarms went to zero across all eight detectors and all six scenarios.

A second correction fell out of the same insight. The live CUSUM ran across the reference
window and the monitoring window continuously, so it entered day 31 with whatever it had
accumulated; the simulated one started at zero. `cusum(reset_at=REFERENCE_DAYS)` makes both
start from the same place. Comparing two statistics requires that they be the same
statistic.

Both properties are pinned by mutants: `bootstrap-ignores-estimation-error` reverts attempt
3, and `calibrate-on-the-whole-series` reverts the reset. Both are killed.
