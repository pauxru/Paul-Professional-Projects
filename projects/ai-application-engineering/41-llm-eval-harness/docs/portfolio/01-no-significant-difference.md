# Why "no significant difference" is the most expensive sentence in LLM evaluation

A team changes a prompt. They run their eval set before and after. The
difference is +0.008, the test comes back p = 0.31, and the summary says "no
significant regression." They ship.

Every step is normal practice. The conclusion does not follow from the
evidence, and this document is about the size of the gap.

## The claim that was not made

"No significant difference" is a statement about a *test*, not about the
world. It says: given this data, we cannot reject the hypothesis that nothing
happened. It does not say nothing happened. Whether the absence of a
detection is informative depends entirely on whether a detection was possible,
and that question is almost never asked.

Put concretely. On the paired variance measured in this project -- a standard
deviation of 0.179 for the item-level difference between two related systems --
a 50-item eval set has **50.9% power** to detect a real +0.05 improvement.
Half of all genuine improvements of that size return "no significant
difference." A team reading that as "no regression" is right by coincidence
slightly more often than a coin.

The number gets worse as the effect gets smaller, which is the direction real
changes trend. Most prompt edits do not move quality by 0.05.

## The three failures this produces, in ascending order of cost

**Reverting good changes.** In section 11 of the report, a real +0.04
improvement, measured once on a 50-item eval set, comes back as a *decline*
8.5% of the time. Not "not significant" -- the wrong sign. One run in twelve
argues for reverting a change that helped.

**Shipping bad ones.** The same arithmetic, reflected. A -0.04 regression is
missed at a similar rate, and it is missed with a written record saying the
eval was run and nothing was found.

**Believing the size of what you did find.** This is the one that compounds.
When power is low, the effects that clear significance are systematically the
ones that got lucky, so the measured effect of a *true* finding overstates it.
At n = 50 the exaggeration factor is **1.39x**. Every significant result is
inflated by about 40% on average, and it is inflated in a way no amount of
care in the analysis can remove, because the selection happened before the
analysis. A quarter's worth of "+3%, +2%, +4%" improvements that sum to a
promised +9% and deliver +6% is not anyone lying.

## Why the eval set is not the variable people think it is

The obvious response is "use more items." That is right, and the cost is worse
than expected: going from detecting +0.05 to detecting +0.01 is not five times
the data. It is **25 times** -- 93 items to 2,309 -- because power scales with
the square of the effect.

But the more useful finding is that the question "is 50 items enough?" has no
answer, because adequacy is not a property of the number 50.

Hold the eval set, the effect, and the judge completely fixed, and vary only
how much the two systems being compared have in common. Power moves from
**28.5% to 79.0%**. Same items, same effect, same statistics.

The mechanism is that paired comparison cancels whatever the two systems
share. A question that is ambiguous is ambiguous for both; a reference answer
that is subtly wrong penalises both. Two prompts against the same base model
share nearly everything, so the paired difference is small and stable and 50
items can be plenty. Two different model families share much less, and the same
50 items are hopeless.

**This means eval set adequacy has to be re-established whenever the kind of
change being tested changes.** A team that validated their eval set on prompt
tweaks has not validated it for a model swap, and nothing in their tooling will
tell them.

The quantity that governs this is the paired standard deviation, and it costs
one afternoon to measure: score two systems on the same items and take the
standard deviation of the per-item differences. Almost nobody has this number,
and every power calculation depends on it.

## What to do instead

Report the distinction explicitly. This project's gate has five verdicts rather
than three, and the added one carries the argument:

> **UNDERPOWERED** -- the observed change of +0.0102 is smaller than the
> 0.0679 this eval set can detect at 80% power. This is not evidence of no
> change; it is an absence of evidence either way.

It does not block. It is a disclosure, not a failure. But it cannot be read as
"no regression," which is the entire point, and it names the number that would
have to change.

The distinction it draws is exactly `|delta| < mde`:

- **INDISTINGUISHABLE** -- the eval set could have seen an effect this size and
  did not. A real null result. Reasonable to ship on.
- **UNDERPOWERED** -- the eval set could not have seen it either way. Nothing
  was learned. Shipping may still be correct, but not *because of this
  measurement*.

Implementing this forces the minimum detectable effect to be computed on every
comparison rather than in an occasional planning spreadsheet, which is most of
the benefit. A team that sees `UNDERPOWERED` on most of their comparisons has
learned something true and previously invisible about their eval set.

## The honest caveat

The MDE is computed from the observed paired standard deviation, which is
itself an estimate, and at small n it is a poor one -- which is precisely when
this verdict fires. The verdict is least reliable exactly where it matters
most.

That is disclosed in `docs/known-limitations.md` rather than papered over,
because the alternative -- suppressing the verdict at small n -- would remove
it from the only place it is needed. A noisy warning about a real problem beats
a silent wrong answer, and this whole document is an argument that the silent
wrong answer is the status quo.
