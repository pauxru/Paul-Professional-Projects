# ADR 003: Split judge error into stable and fresh components

**Status:** Accepted
**Amends:** ADR 002

## Context

`SimulatedJudge` originally keyed its noise on the item id. Every call the
judge made about a given item drew the same perturbation.

In a paired comparison both systems are scored on the same items. The judge's
error on item *i* was therefore identical for the baseline and the candidate,
and subtracted out exactly in the paired difference. Judge noise had no effect
whatsoever on the width of a paired interval.

The consequence was not a crash or a wrong number in isolation. It was a
report that would have concluded **judge quality does not matter for A/B
decisions** -- a conclusion that is false, actionable, and would have been
delivered with confidence intervals around it. It survived because it is
superficially plausible: paired designs *do* cancel systematic judge bias, and
that is a real and useful property. The error was extending it to all judge
error.

The distinction that was missing: a judge's error has a component that is a
property of the *item* -- this question's rubric is ambiguous, so the judge is
lenient about it no matter who answers -- and a component that is a property of
the *response* -- this particular answer sits near the judge's decision
boundary. The first cancels under pairing. The second does not, and it is the
one that scales with how flaky the judge is.

## Decision

Two components, mirroring ADR 002:

```
error = noise * (sqrt(c) * stable + sqrt(1-c) * fresh)
```

`stable` is keyed on the item id and the judge identity. `fresh` is keyed on
the item id, the judge identity, **and the response text**. `c` --
`consistency` -- is the fraction of the judge's error that is a property of
the item rather than of the response.

Keying on the response text is what makes the fresh component differ between
two systems, because two systems produce different text for the same item.

## Consequences

Judge noise now widens paired intervals, monotonically, as it should. With
`consistency = 0.5`, raising noise from 0.001 to 0.20 moves the paired
standard deviation from 0.155 to 0.228. At `consistency = 1.0` it cancels
completely, which is correct and is itself tested -- the stable component
*should* vanish under pairing; that is what makes it stable.

This makes section 8 possible: two judges with identical measured agreement
against humans can require eval sets of very different sizes, because
agreement is a marginal property and what a paired comparison cares about is
the fresh component.

The `consistency` parameter is also a claim about real judges that a
practitioner can check: score the same response twice and see how often the
verdict changes. Most teams have never measured it, and the value determines
how much of their judge's noise their paired design is actually cancelling.

## Consequences for testing

The bug is pinned by
`test_judge_noise_does_not_cancel_completely_in_paired_differences`, which
asserts that paired spread is monotone in judge noise. Note what that test
does *not* do: it does not read the implementation or assert anything about
seeds or keys. It asserts the statistical property the simulation is supposed
to have.

That is the only kind of test that would have caught this. A test asserting
"the judge is deterministic for a given item and response" passes on the buggy
version. A test asserting "noise makes scores vary" passes too. The defect
lived entirely in the *relationship between two runs*, which is invisible to
any test that examines one run.

## Alternatives considered

**Key the noise on the response text alone.** Removes the stable component
entirely, which is the opposite error: real judges are consistently lenient
about certain items, and a design that cancels that consistency would
overstate how much pairing helps.

**Make the judge deterministic and model noise only at the aggregate level.**
Simpler, and it makes section 8 impossible to write -- the whole point there
is that the same aggregate agreement can decompose differently.
