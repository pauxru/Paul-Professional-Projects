# Three attempts to calibrate one CUSUM

Seven of the eight detectors on this panel get their threshold from three lines of code:
take the detector's own scores over the 30-day reference window, take the `1 − ALPHA`
quantile, done. Same rule for everyone, so the comparison measures signal rather than
tuning.

The eighth detector is a CUSUM on the daily refusal rate. It took three attempts, and each
failed attempt taught me something I did not know I was assuming.

---

## Attempt 1: the same rule as everyone else

Run the CUSUM over the reference window, take the 99th percentile of the statistic, use it
as the threshold.

It false-alarmed in all six scenarios. Including `healthy`. Nine false-alarm days on a
scenario where, by construction, nothing happens.

The mistake is a category error, and it is embarrassing in hindsight. Every other detector
on the panel is a *per-day* statistic: it looks at today's data, produces a number, and
forgets. Its distribution over 30 reference days is the same distribution it will have over
60 monitoring days, so a quantile of the first is a sensible threshold for the second.

A CUSUM does not forget. It is a cumulative sum — a reflected random walk — and its
distribution is a function of *how long it has been running*. Asking "how high does this go
in 30 days?" and applying the answer to 60 days asks the wrong question in a way that has a
guaranteed direction: a zero-drift reflected random walk crosses **any** fixed threshold
eventually. It is not a matter of whether, only when. My uniform rule had picked a threshold
and then given the walk twice as long to beat it.

Uniformity was the right principle and I had applied it to a statistic it does not fit. The
principle is "calibrate every detector by the same *procedure*", and the procedure is
"simulate the thing you will actually do". For a memoryless statistic, a reference quantile
*is* that simulation. For a CUSUM it is not.

## Attempt 2: bootstrap the horizon

The textbook fix. Simulate 60 days of data drawn from the reference distribution, run the
same CUSUM over them, record the maximum. Repeat a few thousand times. Take the `1 − ALPHA`
quantile of those maxima. Now the threshold answers "how high does this go in *60* days?",
which is the question.

False alarms fell from nine days to one.

One is a much better number than nine and it is still not zero, and — this is the part worth
keeping — a residual that small is very easy to accept. It looks like Monte-Carlo noise. I
nearly wrote "1 false-alarm day at α = 0.01 over 60 days, which is about what you would
expect" in the README, and that sentence would have been arithmetically plausible and
wrong.

## Attempt 3: bootstrap the reference window too

What made me look again was that the false alarm was not a spike. It was a ramp. The
statistic climbed steadily from around day 40 and crossed. Random walks do spike; they do
not usually ramp.

The live detector does not know the true mean refusal rate. It **estimates** `target` from
30 reference days and then applies that estimate to 60 fresh ones. If the sample mean lands
one standard error low — which happens roughly a third of the time — then every subsequent
day is, on average, above target. A CUSUM integrates. A small constant bias becomes a linear
ramp, and a linear ramp crosses any threshold.

My bootstrap in attempt 2 had used the *true* reference mean, because it had it. So it was
simulating a detector that knows something the real one does not. It calibrated the right
statistic against an easier problem.

The fix is one line of intent and about four of code: **each trial resamples the reference
window and the horizon separately**, estimates `target` from its own resampled reference,
and runs the CUSUM on its own resampled horizon. The simulated statistic now carries the
same estimation error as the live one.

False alarms: zero. Across all eight detectors and all six scenarios. And the CUSUM still
detects `refusal-creep` four days *before* the material day, so the specificity did not come
out of its sensitivity.

A second correction fell out of the same insight. The live CUSUM had been running
continuously from day 1, so it entered day 31 carrying whatever it had accumulated, while
every simulated trial started at zero. `cusum(reset_at=REFERENCE_DAYS)` makes both start
from the same place. Comparing two statistics requires that they be the same statistic, and
"same formula" is not the same as "same statistic" when the formula has state.

---

## What I actually took from it

The lesson is not about CUSUMs. It is that **a calibration is a simulation of your detector,
and it must simulate the detector's ignorance, not just its arithmetic.** Attempt 2's
bootstrap was correct code computing a correct quantile of the wrong random variable,
because it had access to a parameter the deployed system has to estimate. Every place I
calibrate a threshold against historical data, the question to ask is: what does the live
detector *not know* that my calibration knows? Here it was one number, the mean, and one
number was enough to put a ramp through the threshold.

Both properties are pinned by mutants. `bootstrap-ignores-estimation-error` reverts attempt
3 to attempt 2; `calibrate-on-the-whole-series` removes the reset. Both are killed, which
means both fixes are load-bearing rather than decorative — a distinction I could not have
made by reading the code, since attempt 2's code looks entirely reasonable and produces a
number.
