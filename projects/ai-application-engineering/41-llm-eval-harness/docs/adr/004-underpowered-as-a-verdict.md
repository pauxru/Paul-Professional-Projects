# ADR 004: Report UNDERPOWERED as a distinct verdict

**Status:** Accepted

## Context

The conventional A/B gate has three outcomes: better, worse, or no
significant difference. The third is where the damage is done, because it is
routinely read as "no regression" and those are different claims.

"No significant difference" collapses two situations that call for opposite
responses:

1. **The eval set could have detected an effect of this size, and did not.**
   This is a real null result. It is evidence, and shipping is reasonable.
2. **The eval set could not have detected an effect of this size either way.**
   This is not evidence of anything. The measurement was not capable of
   answering the question that was asked of it.

The report puts numbers on how often the second case is mistaken for the
first. At n = 50 the power to see a +0.05 improvement is 50.9%; roughly half
of genuine improvements of that size return "no significant difference."
A team reading that as "no regression" is right by luck.

## Decision

Five verdicts, not three. Compute the minimum detectable effect from the
observed paired standard deviation and the actual n, and when a
non-significant result has `|delta| < mde`, return `UNDERPOWERED` with a
reason that states the MDE:

> the observed change of +0.0102 is smaller than the 0.0679 this eval set can
> detect at 80% power. This is not evidence of no change; it is an absence of
> evidence either way

`UNDERPOWERED` does not block. It is not a failure; it is a disclosure.

## Consequences

**It shifts the argument from statistics to eval set design.** A gate that
frequently returns `UNDERPOWERED` is telling its owners that their eval set is
too small for the changes they are making, in a form that names the size they
would need. Section 3 turns that into a number: the same eval set that
detects +0.05 with 93 items needs 2,309 for +0.01. That 25x is the fact that
makes the verdict actionable rather than merely honest.

**It has a real cost.** The MDE is computed from the *observed* paired
standard deviation, which is itself an estimate, and at small n it is a poor
one -- which is precisely where the verdict fires most often. The verdict is
therefore least reliable exactly where it matters most. This is disclosed in
`docs/known-limitations.md` rather than papered over. The alternative --
suppressing the verdict at small n -- would remove it from the only place it
is needed.

**It puts a name on a boundary that is otherwise invisible.** The distinction
between `INDISTINGUISHABLE` and `UNDERPOWERED` is exactly `|delta| < mde`, and
having to write that comparison forced the MDE to be computed and reported on
every comparison rather than in an occasional planning spreadsheet.

## The calibration this required

A verdict that is not calibrated is decoration. Section 12 runs 200 trials per
scenario at n = 120:

| true effect | REGRESSED | IMPROVED | UNDERPOWERED |
|---|---|---|---|
| -0.06 (harmful) | 97% | 0% | -- |
| 0.00 (no-op) | -- | 1.5% | 92% |
| +0.06 (helpful) | -- | 92% | -- |

The 0% is the number worth looking at: a genuinely harmful change is never
called an improvement. The 1.5% is the false-positive rate against a nominal
5% alpha, conservative because the BCa interval is conservative at this n.

## Alternatives considered

**Return a power figure alongside a three-valued verdict.** Strictly more
information and strictly less effective. A number in a field next to a verdict
does not change what anyone does; a verdict named `UNDERPOWERED` does.

**Block on UNDERPOWERED.** Fails closed, which is usually the right instinct
and is wrong here: most changes to most systems are small, so most comparisons
on a realistic eval set are underpowered, and a gate that blocks them all
stops all work and is disabled within a week.

**Widen the tolerance instead, so small effects pass explicitly.** Conflates
"we do not care about effects this small" with "we cannot see effects this
small." The first is a product decision and belongs in
`regression_tolerance`, which exists and is separate. The second is a property
of the measurement.
