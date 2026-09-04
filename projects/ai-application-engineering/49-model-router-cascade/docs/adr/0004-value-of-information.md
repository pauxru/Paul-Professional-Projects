# ADR 0004 — Escalate on value per cent, not on a confidence threshold

**Status:** accepted
**Date:** 2024-06

## Context

Every cascade in the literature and in every vendor blog post uses the same rule:
call the cheap model, and escalate if its confidence is below some threshold `T`.

That rule has a blind spot, and I did not see it until I fixed a *different*
problem. My first workload had prompt sizes spanning only about 12x, and token
count correlated strongly with difficulty, because I generated tokens from word
count and word count from difficulty. In that workload a fixed threshold is
nearly optimal by accident, and the cost-aware rule I had built showed almost no
gain. I nearly deleted it.

The workload was wrong, not the rule. Real request streams have a component of
prompt size that has nothing to do with how hard the question is: a pasted stack
trace, a forty-page contract, a whole email thread. I added `ContextTokens` —
lognormal, present on 38% of queries, drawn **independently of difficulty**:

```
prompt size:  p1 44 tokens,  p50 270,  p99 4,567      -> 104x spread
corr(log tokens, difficulty) = +0.310                 -> explains 10% of variance
```

Now the blind spot is visible. A fixed threshold spends the same confidence
budget on a 44-token lookup and a 4,567-token document, though escalating the
second costs a hundred times more and is barely more likely to need it. The
question was never "am I unsure enough". It is **"is what I would learn worth
what it costs"**.

## Decision

```
escalate when   ( P(large is right) − confidence ) / marginal_cost   >=   λ
```

- The numerator is the expected accuracy gain from escalating: the large model's
  measured accuracy (estimated on the **training** split — a production system
  gets this from an eval set) minus this query's confidence.
- The denominator is what this particular escalation costs, which is
  proportional to this query's prompt size.
- **λ is accuracy points per cent.** It is a price, not a knob, and unlike a
  confidence threshold it is a quantity a finance conversation can be had about.
  "We pay up to 0.18 accuracy points per cent" is a sentence a CFO can respond to.

## Measured

```
policy                cost   accuracy   escalated   vs hull   cost saved
cascade-s>l@0.91     3732¢     86.2%         78%    +2.5 pts        +12%
value-raw@0.18       2226¢     82.6%         66%    +6.4 pts        +37%
value-cal@0.18       2281¢     83.6%         73%    +7.1 pts        +39%
```

**+3.9 points and 25 percentage points of the bill, from one division.** Same
models, same confidence signal, same escalation target. It is the largest single
improvement in the report, and it is arithmetic, not machine learning.

## Testing the mechanism, not the aggregate

My first test asserted that the value rule escalates *shorter* queries on median.
**It failed**, and the failure was correct: escalated queries had a median of 291
tokens against 189 for kept ones.

The reason is a confounder. Difficulty and length are positively correlated
(+0.31), so the queries a cost-aware rule most wants to escalate — the hard ones
— are also longer on average. The cost-awareness is a **conditional** effect at
equal confidence, not an unconditional preference for short prompts. A reader who
assumed otherwise would misread the result, so
`TestBothRulesEscalateLongerQueriesOnMedian` now pins the confounder explicitly.

The mechanism is tested exactly instead, and this is only possible because of ADR
0002. `Ask` is a pure function of `(seed, query ID, model, difficulty)` and does
**not** depend on token count. So two queries can be constructed differing *only*
in `Tokens`: identical confidence, identical correctness, different marginal cost.
`TestValueCascadePricesTheEscalation` asserts the rule escalates the 200-token one
and declines the 20,000-token one at the same λ. That is the mechanism, isolated,
with no statistics involved.

The economic claim gets its own distributional test:
`TestValueCascadeBuysACheaperEscalationSetAtTheSameRate` finds the fixed threshold
whose escalation *rate* matches the value rule's, and asserts the value rule's
total bill is lower — it buys a cheaper subset of the same size.

## Consequences

- The rule needs an estimate of `P(large is right)`. I fit it on the training
  split; in production it comes from an eval set and it will drift. A stale
  estimate biases λ's meaning, and the rule degrades gracefully rather than
  failing — but it does degrade. Noted in known limitations.
- Because the confidence is now **subtracted** rather than **compared**, the rule
  is no longer invariant to monotone transforms of the signal, which is exactly
  the boundary ADR 0003 draws. That is not a downside; it is why calibration can
  matter here at all.
- λ is not a probability and cannot be set by intuition about confidence. It has
  to be swept once against the fleet's price list. The report sweeps 16 values
  and reports mean lift and a win count alongside the best point, because
  best-point comparisons reward grid luck.

## Alternatives considered

**Threshold on confidence, but a different threshold per size bucket.** Equivalent
in spirit and worse in practice: it needs bucket boundaries, it has a
discontinuity at each one, and it has more parameters than the thing it
approximates. The value rule is the continuous limit of it with one parameter.

**Optimise the escalation set under a hard budget (a knapsack).** Strictly better
offline, and unusable online — it needs the whole batch before deciding anything.
The value rule is the greedy per-query approximation, and λ is precisely the
Lagrange multiplier the knapsack would find.
