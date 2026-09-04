# 1 — What a routing result actually claims

Almost every LLM routing result you will read has this shape:

> Our router sends 30% of traffic to GPT-4 and 70% to a small open model. It
> achieves 82% of GPT-4's quality at 40% of the cost.

Read it again. It contains no comparison. It is a description of a configuration
and its outcome, and there is no way to tell from it whether the router did
anything at all.

The missing question is: **what does the same money buy with no intelligence?**

## The obvious baseline, and why I used it first

Send x% of traffic to the big model *at random*. This is the right instinct: a
router's only job is to pick *which* queries to escalate, so a router that picks
them at random is the definition of contributing nothing.

Geometrically, random escalation traces the straight chord between the two
endpoint policies in cost/quality space. That is easy to state and worth
verifying rather than assuming, so the report measures it:

```
always-small                45¢   43.0%
random-to-large@10%        454¢   47.4%      on the chord
random-to-large@25%       1154¢   54.4%      on the chord
random-to-large@50%       2276¢   65.3%      on the chord
random-to-large@75%       3450¢   76.4%      0.6 pts below the chord
always-large              4501¢   87.5%
```

Random lands on the chord to within 0.6 points at every rate. The arithmetic is
right.

And the baseline is still wrong.

## The row that changed the project

I added a mid-tier model to the fleet because I wanted to build a three-stage
cascade. Before using it, I printed its fixed-policy row:

```
always-mid                 675¢   68.5%      +19.3 points ABOVE the chord
```

Nineteen points. Not from routing — from picking a different model and sending
everything to it.

This is not a quirk of my parameters. Model quality rises roughly with the *log*
of cost, so marginal accuracy per cent falls as you go up the fleet, so the
cost/quality curve is **convex**. Whenever it is, an intermediate model sits above
the endpoint chord, and a random mix of small and mid beats a random mix of small
and large at every budget in between.

Which means the chord is not the zero-information baseline. The **upper convex
hull** is:

```
hull vertex        cost   accuracy
always-small         45      43.0%
always-mid          675      68.5%
always-large       4501      87.5%
```

Every point on that hull is attainable by randomly mixing two fixed policies. Any
router landing below it is beaten by a coin flip between two models off a price
list.

## What it did to my results

The flagship cascade, scored both ways:

```
                              vs chord      vs hull
cascade-small>large           +6.4 pts     +2.5 pts
```

Same policy. Same data. Same run. One framing is a solid result worth writing up
and the other is a rounding error, and only one of them is true.

The learned classifier fared worse: **+0.3 points against the hull**. It has a
held-out AUC of 0.825 as a difficulty predictor — it learned something genuinely
real — and as a *router* it is worth nothing, because at the operating point
where it is accurate it escalates 95% of traffic and has become always-large with
extra steps.

That is the finding. Not "my router works", but:

> "We beat random escalation to the frontier model" and "we are worse than just
> using the mid-tier model" can both be true at once. A team can ship a router
> that loses money while celebrating the first one.

## The ceiling, which is a different thing

The report also computes an oracle: escalate exactly when the small model would
be wrong and the large model would be right. Unimplementable by construction — it
knows the answer before it asks the question.

It is reported as a **ceiling**, never as a baseline. The distinction matters. The
gap from a router *up* to the oracle says how much headroom the signal has left.
The gap from a router *down* to the hull says whether it should exist. Confusing
them is how "we captured 59% of the achievable gain" gets written about a policy
that is below the hull.

At the champion's spend, the hull gets 76.5% and the oracle gets 88.5%. The best
real router in this report reaches 83.6% — **59% of a 12-point headroom**, at 51%
of the always-large bill. Both numbers are needed to say anything.

## The transferable part

This generalises past routing. Any system that spends a resource to improve an
outcome — a cache, a retry policy, a human review queue, a fraud check — has a
zero-information baseline that is *some mixture of the trivial policies*, and it
is almost never the two-endpoint chord.

The three questions worth asking of any such result:

1. **What is the full set of trivial policies?** Not two. All of them. Every
   fixed choice available without building anything.
2. **What does their upper convex hull achieve at my system's spend?** That is
   the number to beat, because mixing attains it for free.
3. **What is the ceiling, and how far from it am I?** Distance to the ceiling
   tells you whether to keep tuning the policy or go find a better signal.

Question 1 is the one that gets skipped, and skipping it is what made a
19-point difference in this report.
