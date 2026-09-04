# The date is not the date

Every programme plan has a date on it. This one says month 12.

Run the schedule ten thousand times with uncertainty on team effort and the
mean outcome is month 12.43, the P90 is 15.75, and the P95 is 16.54. The date
on the slide is not a central estimate of anything. It is roughly a P35.

That is unsurprising. What is worth writing down is *why*, because the usual
explanation is wrong, and because the standard way of reporting the answer is
broken in a way that is easy to miss.

## The bias is not padding coming back out

The comfortable explanation for schedule overrun is that estimates are
optimistic. Teams pad, managers cut the padding, reality arrives.

This model rules that out by construction. Team effort is drawn from a
log-normal with a median correction: `estimate * exp(sigma*z - sigma^2/2)`.
That `- sigma^2/2` term makes the *mean* of the multiplier exactly 1.0, so
every team's expected effort equals its point estimate. There is no optimism
in the inputs.

The bias is still there. It comes from the structure of the schedule, not from
the estimates.

A wave ends when its *slowest* team ends. A wave with five teams working in
parallel does not take the expected duration of the average team; it takes the
expected duration of the maximum, and the mean of a maximum exceeds the
maximum of the means. Every wave boundary is a synchronisation point that
converts variance into delay, and the delay does not average out across waves
— it accumulates, because each wave's late finish is the next wave's late
start.

## The bias comes from balance, not headcount

The obvious prediction is that bias grows with the number of teams in a wave.
More parallel streams, more chances for one to be late.

I wrote that prediction down before measuring it, and it is wrong.

The predictor that works is the number of **near-critical streams**: teams
whose deterministic duration is within 20% of the wave's longest. Those are
the streams that can plausibly become the binding one under a draw. A team
with a two-week task in a wave whose critical path is three months contributes
nothing to the maximum no matter how badly it goes.

- near-critical stream count vs bias: Spearman **0.975**
- team count vs bias: Spearman **0.821**

The clean demonstration is inside the best plan: waves 1 and 3 both run five
teams, and their biases differ by a factor of **2.4**. Headcount is identical.
Balance is not.

This has a direct operational reading. If you want a wave to land on time, you
do not reduce the number of teams in it — you make one stream unambiguously
the critical one and give everything else slack. A perfectly balanced wave,
which looks like good resource utilisation on a chart, is the worst case for
schedule reliability: every stream is a candidate for being the late one.

## The extension I expected to hold, and did not

If merge bias comes from balance, and a cost optimiser packs waves tightly to
minimise overhead, then the optimiser should be *manufacturing* balance and
therefore manufacturing risk. The cheapest plan should be the most
schedule-biased.

That is a good story. I measured it and it is not true.

The annealed plan's bias sits at +1.394 months, **between** `dependents_first`
at +1.264 and `least_critical_first` at +1.528. The spread across all planners
is 0.264 months against a Monte Carlo standard error of 0.0189 — the
differences are real and measurable, and they do not order the way the
hypothesis requires.

I reported it as contradicted and left it in. A rejected extension is worth
more than a missing one: the mechanism in the previous section is now known to
be a property of *wave composition* rather than a property of *cost
optimisation*, which is a narrower and more defensible claim than the one I
set out to make.

## Change freezes are a cost line, not a hedge

Cutovers are forbidden in months 2, 10 and 11 — year-end close and the January
renewal peak. This is standard practice in insurance and it is universally
described as risk management.

It costs **2.10 months**, 21.3% of the schedule. That much is expected; you
are forbidding work.

The part that is not expected: the standard deviation of the end date **rises**,
from 2.435 to 2.675 months. Freezes do not reduce schedule uncertainty. They
increase it, because a cutover that would have landed in month 10 does not
slip a little — it slips to month 12, and whether it slips at all depends on
exactly where the preceding waves landed. Freezes convert small variations
into discrete jumps.

A change freeze reduces *business* risk during a sensitive period. It is not a
hedge against schedule risk, and reporting it as one is a category error that
costs two months and buys negative schedule certainty.

## The P50 can read zero slip on a plan that is months late

This is the part I did not expect to find and would not have found without a
test.

A test asserted the merge bias was positive. It came back exactly `0.0`.

Freeze months 10 and 11 are adjacent. Any draw whose unconstrained end lands
anywhere in that two-month window is pushed to the same date: month 12. The
outcome distribution is not continuous. It has an **atom** of probability at
the trailing edge of every freeze run, as wide as the freeze.

On the `dependents_first` plan, **30.4% of all outcomes land on exactly month
12.00**. The median falls inside that atom. So:

> `P50 − plan date` reads **+0.00 months**.
> `mean − plan date` is **+0.84 months**.

Same distribution. Same plan. One of those two statistics reports a programme
delivering exactly to plan.

The mechanism generalises well beyond change freezes. Any delivery constraint
that quantises outcomes — quarterly release trains, monthly regulatory
windows, an annual maintenance weekend — produces atoms, and a percentile
statistic read across an atom is not measuring what it appears to measure. A
great deal of enterprise delivery reporting is quoted in percentiles against
exactly these constraints.

There is a second-order effect the same table exposes. With freezes off, the
plan date is 9.90 and the mean slip is +1.39. With freezes on, the plan date
is 12.00 and the mean slip is +0.43. Freezes make the *reported* bias smaller
while making the actual date later, because the deterministic plan has already
absorbed the slippage the simulation would otherwise have discovered. A
programme could report improved schedule confidence by adding freezes.

## The assumption is worth more than the estimates

The correlation structure has two levels: a programme-wide shock and a
per-wave shock. Sweeping both across a plausible range moves the P90 between
13.59 and 15.15 months — a **1.57-month spread** driven entirely by a
parameter nobody in the programme has data for.

That is larger than several of the effects the model is being used to compare.

The two levels also push in opposite directions, which is why the model needs
both. Programme-level correlation lengthens the tail: a shock hitting
everything cannot be averaged away. Wave-level correlation *shortens* it: a
wave's duration is a maximum over its teams, and correlating those teams makes
the maximum behave more like a single draw than a max of independents. A
single correlation parameter would have cancelled these into an
uninterpretable middle.

The honest reporting position is that the P90 is a function of an assumption,
the assumption is stated, and the sensitivity to it is published next to the
number.

## What to take from this

- A deterministic plan date on a multi-team programme is roughly a P35, and
  the gap is structural rather than a failure of estimation.
- Balance within a wave predicts schedule bias; headcount does not. Perfectly
  utilised waves are the least reliable ones.
- Change freezes cost schedule *and* increase schedule variance. They are
  business-risk management, not schedule-risk management.
- Check whether your delivery constraints quantise outcomes before quoting a
  percentile. If they do, quote the mean as well, and look for the atom.
