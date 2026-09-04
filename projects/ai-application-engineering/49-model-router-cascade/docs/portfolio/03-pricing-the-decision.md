# 3 — Pricing the decision

Section 4 of the report is about something that does nothing. This one is about
the thing that worked, and it is a division.

## The blind spot

Every cascade uses the same rule: call the cheap model, escalate if its
confidence is below `T`.

That rule asks *"am I unsure enough?"* It never asks *"is finding out worth what
it costs?"* — and in a real workload those are wildly different questions,
because escalation cost is proportional to prompt size and prompt size is not
proportional to anything the confidence knows about.

```
prompt size:   p1  44 tokens
               p50 270
               p99 4,567             ->  104x spread

corr(log tokens, latent difficulty) = +0.310
                                      ->  size explains 10% of difficulty
```

A fixed threshold spends the same confidence budget on both ends of that
distribution. Escalating the 4,567-token query costs a hundred times more than
escalating the 44-token one, and it is only marginally more likely to need it.
The person who pastes a forty-page contract is frequently asking the easiest
question in the queue.

## I almost deleted this rule

My first workload generated tokens from word count and word count from
difficulty. Prompt size spanned about 12x and correlated strongly with
difficulty. In that world a fixed threshold is nearly optimal *by accident* — the
expensive queries really are the hard ones — and the value rule I had written
showed almost no improvement. I wrote it off as an idea that sounded better than
it was.

The workload was wrong, not the rule. Real request streams contain a large
component of prompt size that has nothing to do with question difficulty: a
pasted stack trace, a contract, an email thread. Whatever the user had in their
clipboard. So I added `ContextTokens` — lognormal, on 38% of queries, drawn
**independently of difficulty**.

That single change is what made the rest of this document possible, and it is
worth stating plainly: **the result depended more on getting the workload right
than on getting the algorithm right.** An experiment can only find effects its
inputs are capable of exhibiting, and a generator that quietly ties cost to
difficulty has assumed away the entire question.

## The rule

```
escalate when   ( P(large is right) − confidence ) / marginal_cost   >=   λ
```

- Numerator: expected accuracy gain. The large model's measured accuracy
  (estimated on the *training* split — production gets this from an eval set)
  minus this query's confidence.
- Denominator: what escalating *this* query costs.
- **λ is accuracy points per cent.** It is a price, not a knob. "We pay up to
  0.18 accuracy points per cent" is a sentence a CFO can respond to. "Our
  confidence threshold is 0.91" is not.

## What it bought

```
policy              cost   accuracy   escalated   vs hull   cost saved
cascade-s>l@0.91   3732¢     86.2%         78%    +2.5 pts       +12%
value-raw@0.18     2226¢     82.6%         66%    +6.4 pts       +37%
value-cal@0.18     2281¢     83.6%         73%    +7.1 pts       +39%
```

**+3.9 points and 25 percentage points of the bill.** Same models, same
confidence signal, same escalation target. One division.

It is the largest single improvement in the report, and there is no machine
learning in it.

## Testing a conditional effect

My first test asserted the obvious thing: the value rule escalates shorter
queries. **It failed** — escalated queries had a median of 291 tokens against 189
for kept ones.

The test was wrong and the failure was informative. Difficulty and length are
positively correlated (+0.31), so the queries a cost-aware rule most wants to
escalate are *also* longer on average. The cost-awareness is a **conditional**
effect — cheaper escalations preferred *at equal confidence* — not an
unconditional preference for short prompts.

A reader who assumed otherwise would misread the whole section, so there is now a
test named `TestBothRulesEscalateLongerQueriesOnMedian` that pins the confounder
deliberately. Documenting a counterintuitive fact as an assertion is worth more
than documenting it in prose, because the assertion notices when it stops being
true.

The mechanism gets tested exactly instead, and this is only possible because the
simulator is deterministic in `(seed, query ID, model)` and does **not** depend on
token count. Two queries can therefore be constructed that differ *only* in
`Tokens`: identical confidence, identical correctness, different marginal cost.

```go
cheap.Tokens = 200
dear.Tokens  = 20000
// same ID, same difficulty  ->  byte-identical confidence

if !v.Route(cheap, o).Escalated { t.Error("declined an escalation it could afford") }
if  v.Route(dear,  o).Escalated { t.Error("escalated at 100x the price, same confidence") }
```

No statistics. The mechanism, isolated.

The economic claim gets its own distributional test: find the fixed threshold
whose escalation *rate* matches the value rule's, and assert the value rule's
total bill is lower. Same number of escalations, cheaper ones.

## Why this generalises

The pattern is: **a decision rule that spends a variable amount should divide by
what it is about to spend.** It shows up wherever a system pays a non-uniform
price for information:

- **Cache admission.** Admit on expected hit rate ÷ bytes, not on hit rate.
- **Retry policy.** Retry on P(success) ÷ cost of the attempt, not on a fixed
  attempt count — a retry of a 20-second query is not a retry of a 200ms one.
- **Human review queues.** Route on expected error caught ÷ reviewer minutes.
- **Fraud checks.** Run the expensive check on value at risk ÷ check cost.

In every case the common implementation thresholds the numerator alone, and in
every case the denominator varies by orders of magnitude. The fix is one
division, and λ — the multiplier — is the price the business is willing to pay,
which is a far better thing to argue about in a planning meeting than a threshold
nobody can interpret.

There is also a theoretical footnote worth knowing: λ is exactly the Lagrange
multiplier that a knapsack solver would find if you could optimise the escalation
set offline under a hard budget. The value rule is the greedy per-query
approximation to that knapsack, which is why it works and why it needs only one
parameter.

## The second-order consequence

Because the rule **subtracts** the confidence rather than **comparing** it, it is
no longer invariant to monotone transforms — which is precisely the boundary drawn
in the calibration write-up. Making the rule cost-aware is what made calibration
capable of mattering at all.

It still barely does: +0.7 at the best λ, +0.08 averaged over the sweep, winning
at 7 of 16. But it went from *provably zero* to *measurably small*, and the reason
is structural rather than empirical. That is the kind of thing worth knowing
before funding a calibration project.
