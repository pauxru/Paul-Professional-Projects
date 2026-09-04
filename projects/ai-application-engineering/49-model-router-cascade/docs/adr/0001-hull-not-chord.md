# ADR 0001 — The baseline is the convex hull, not the chord

**Status:** accepted
**Date:** 2024-06

## Context

A routing result is a comparison. "30% of traffic to the large model, 82%
accuracy, 40% of the cost" is not a finding until you know what the same money
buys with no intelligence at all.

The near-universal baseline in routing work is **random escalation**: send x% of
traffic to the expensive model, chosen at random. Geometrically this traces the
straight chord between the two endpoint policies in cost/quality space, and I
verified that empirically before relying on it — `random-to-large` at 10%, 25%,
50% and 75% all landed on the chord to within 0.60 accuracy points.

So the chord is arithmetically correct. I nearly stopped there.

The problem showed up when I added a mid-tier model to the fleet for the
three-stage cascade and printed its fixed-policy row:

```
always-small      45¢   43.0%
always-mid       675¢   68.5%      <- +19.3 points above the small→large chord
always-large    4501¢   87.5%
```

`always-mid` beats the chord by nineteen points. Not the router — the *baseline*,
by doing nothing at all except picking a different model.

This is not a quirk of my parameters. It is what happens whenever the fleet's
cost/quality curve is **convex**, which is the normal case: model quality rises
roughly with the log of cost, so the marginal accuracy per cent falls as you go
up the fleet. Any intermediate model that sits above the endpoint chord makes
that chord a strictly too-weak baseline.

The consequence is severe. Measured against the chord, my flagship cascade gains
+6.4 points and looks like a clear win. Measured properly it gains +2.5, and
several of the policies I would otherwise have reported as successes are worse
than simply routing everything to the mid-tier model.

## Decision

**The baseline is the upper convex hull of all fixed single-model policies.**

Every "vs baseline" and "cost saved" figure in the report is computed against
that hull, interpolated at the policy's own spend.

Rationale: a random mixture of two fixed policies attains every point on the
segment joining them, so **the whole hull is attainable with zero information**.
Any point below the hull is beaten by a coin flip between two fixed models. A
router that lands there is not merely unimpressive; it is actively worse than
having built nothing.

`frontier.BuildHull` computes it with a monotone chain over the fixed policies
sorted by cost, keeping only vertices that turn the right way.

`frontier.Line` — the chord — is deliberately **kept in the codebase**, used in
exactly one place: section 2 of the report prints the same policy's lift against
both baselines side by side, so the reader sees +6.4 and +2.5 in adjacent
columns. The wrong baseline is more instructive when it is visible.

## Consequences

- Several policies I built lose their claim. `classifier@0.10` is +0.3 points
  against the hull, which is nothing. That is the correct result and it is
  reported as such.
- The hull must be recomputed whenever the fleet changes, and the report asserts
  which models are hull vertices before the three-stage cascade is used at all —
  if the middle model is *not* on the hull, a middle cascade stage is a pure tax.
- The comparison is now against a baseline that improves as the fleet grows,
  which is the right incentive: adding a mid-tier model to your fleet should
  raise the bar your router has to clear.

## Alternatives considered

**Compare against always-large on cost alone.** This is the most common framing
in vendor material and it is trivially winnable — any cascade is cheaper than
always-large. It says nothing about whether the money was well spent.

**Compare against random escalation only.** What I started with. Correct as far
as it goes and wrong in the way described above.

**Compare against the oracle.** Reported, as a *ceiling* (the best real router
captures 59% of the hull→oracle headroom). It cannot be the baseline: the oracle
knows the answer before it asks the question, so the gap to it conflates "my
router is bad" with "this signal is fundamentally limited".
