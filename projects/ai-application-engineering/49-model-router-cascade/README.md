# Model Router & Cascade — what actually moves the cost/quality frontier

A measurement harness for LLM routing policies: cheap-model-first cascades,
learned difficulty classifiers, three-stage chains, and a cost-aware value rule
— all scored against a baseline that most published routing results get wrong.

Written in Go 1.23, standard library only. 120 tests. `go vet` clean.

```powershell
.\test.ps1 -Runs 2      # vet + full suite, twice, to prove determinism
.\demo.ps1 -Save        # runs the experiment, writes docs/results.md
```

Full measured output: **[docs/results.md](docs/results.md)**.

---

## The honest disclaimer, up front

**No language model is called.** `internal/models` is a deterministic simulator.

That is a real limitation and the README is not going to bury it. What it means
in practice:

- Every *absolute* number here — 86.2% accuracy, 3732¢ — is a property of the
  simulator, not of GPT-4 or Llama. Do not quote them.
- Every *structural* result — that temperature scaling cannot move a threshold
  cascade's frontier, that the convex hull is the correct baseline, that
  dividing by marginal cost beats a fixed threshold — is a property of the
  **algorithms**, and holds for any fleet with the same qualitative shape:
  bigger models cost more and are more likely to be right, confidence ranks
  better than it calibrates, and prompt size varies far more than difficulty does.

The simulator is built so those three properties hold and nothing else is
assumed. [docs/known-limitations.md](docs/known-limitations.md) goes through
each conclusion and says which side of that line it falls on.

---

## The four findings

### 1. The baseline decided the verdict, not the algorithm

The standard baseline for a router is *random escalation*: send x% of traffic to
the big model at random. It traces the straight chord between "always cheap" and
"always expensive" in cost/quality space, and I verified it does — random landed
on the chord to within 0.6 points at every rate I tried.

It is still the wrong baseline, and this is where the experiment turned:

```
always-small                45¢   43.0%
always-mid                 675¢   68.5%   <- +19.3 points ABOVE the chord
always-large              4501¢   87.5%
```

The fleet's cost/quality curve is **convex**. A random mix of small and mid beats
a random mix of small and large at every budget in between — with no routing
logic at all. The correct zero-information baseline is the **upper convex hull**
of the fixed policies, which random mixing attains at any budget.

Scored against the chord, the flagship cascade gains **+6.4 points**. Scored
against the hull, **+2.5**. Same policy, same data, opposite verdict.

> "We beat random escalation to the frontier model" and "we are worse than just
> using the mid-tier model" can both be true at once. A team can ship a router
> that loses money while celebrating the first one.

`internal/frontier` computes the hull with a monotone chain. `Line` — the chord —
is kept in the codebase purely so the report can show the wrong baseline next to
the right one.

### 2. Recalibrating the confidence moved the frontier by exactly zero

The small model's confidence is badly calibrated: **ECE 0.059, worst bin off by
0.226**, and it ranks well — **AUC 0.918**. Those are different failures, and the
textbook response to the first is temperature scaling.

So I fitted one on the training split. It worked: fitted temperature **1.59**
against a simulator truth of 1.90, **ECE fell 51%**, Brier improved. And:

```
AUC   0.9177  ->  0.9177
48 of 48 threshold pairs made byte-identical decisions on all 4,000 queries.
Largest disagreement: 0.
```

A threshold cascade only ever evaluates `confidence >= T`. Temperature scaling is
**monotone**. A monotone transform cannot change which queries clear a bar — only
which number on the dial names that set. Recalibration is an exact
*reparameterisation* of the threshold, and `TestRecalibrationIsAReparameterisation`
asserts it per query, per threshold, at four temperatures.

Because `logit(temper(p,t)) = logit(p)/t`, the map `temper(·, 1/t)` is the exact
inverse, so the corresponding raw threshold is available in closed form.

This is not an argument against calibration. It makes the knob *mean* something:
"escalate below 0.90" on the scaled signal really does escalate the queries the
small model gets right less than 90% of the time, so the threshold can be set
from a product requirement instead of tuned against a spreadsheet. That is worth
doing. It is not a quality win, and reporting it as one is how a team spends a
quarter on calibration and ships an unchanged product.

### 3. What did move the frontier was dividing by the cost

Prompt size in this workload spans **104x** (p1 44 tokens, p99 4,567) and
correlates with difficulty at only **+0.31** — it explains 10% of the variance.
The person who pastes a forty-page contract is often asking the easiest question
in the queue.

A fixed confidence threshold is blind to this. It spends the same confidence
budget on a 44-token lookup and a 4,567-token document, though escalating the
second costs a hundred times more and is barely more likely to need it.

```
escalate when  (P(large is right) − confidence) / marginal_cost  >=  λ
```

λ is *accuracy points per cent* — a price, which is a quantity a finance
conversation can be had about, unlike a confidence threshold.

```
cascade-s>l@0.91     3732¢   86.2%   +2.5 pts   12% cost saved vs hull
value-raw@0.18       2226¢   82.6%   +6.4 pts   37% cost saved vs hull
value-cal@0.18       2281¢   83.6%   +7.1 pts   39% cost saved vs hull
```

**+3.9 points and 25 percentage points of the bill, from one division.** Same
models, same signal, same escalation target.

And note the second-order consequence: the value rule *subtracts* the confidence
rather than comparing it, so it is **not** monotone-invariant, and unlike the
threshold rule it genuinely can be affected by recalibration. Whether calibration
matters turns out to be a property of **how the rule consumes the signal**, not
of the signal.

Measured honestly, that effect is small: +0.7 points at the best λ, but only
+0.08 averaged over 16 λ values, winning at 7 of 16. Real, and inside the noise.
The report says so rather than quoting the best point.

### 4. The failure mode nobody designs for is that it works

A cost-optimising router concentrates traffic on whichever model answers well.
Then that provider degrades, and every request retries into a timeout. The
routing policy is correct throughout and the service is down.

`internal/budget` is the operational half: per-tenant spend caps in **integer
tenths of a cent** (`TestSpendDoesNotDrift` runs 1,000,000 reservations and
asserts exactness), failover, graceful degradation, and a circuit breaker whose
half-open state requires *N consecutive* successful probes.

```
4000 requests: 2842 served, 745 failed over, 1109 degraded, 49 rejected
served + degraded + denied == 4000   ->  asserted, true
```

Two details that are easy to get wrong:

- **acme burns its cap and is then served on the small model rather than being
  rejected.** That degraded count is the number to alert on — a customer is
  silently getting a worse product and nothing in the error rate will show it.
- **The false dawn.** The breaker's recorded transition sequence contains four
  `open → half-open → open  (probe failed)` cycles before it finally closes. One
  passing probe must not restore full traffic to a provider that is not well yet.
  The tests assert the *transition sequence*, not the final state — most breaker
  bugs live in the path, not the destination.

---

## Layout

```
cmd/router/          the six-section experiment driver
internal/workload/   query stream with a hidden latent difficulty
internal/models/     deterministic fleet simulator (small / mid / large)
internal/calibration ECE, MCE, Brier, AUC, temperature fitting, reliability diagram
internal/frontier/   convex hull, Pareto frontier, ASCII plot
internal/router/     the policies: Single, Random, Oracle, Classifier,
                     Cascade, Cascade3, ValueCascade
internal/budget/     spend caps, failover, degradation, circuit breaker
```

**Determinism** is keyed on `(seed, query ID, model name)`, so two policies that
make the same call on the same query get the same answer. Without that, a
3-point difference between policies is buried in sampling noise. Every number in
`docs/results.md` reproduces byte for byte.

**No policy may read `Query.Difficulty`.** The oracle baseline may, and it is
labelled a ceiling for exactly that reason.
`TestClassifierScoreDependsOnlyOnFeatures` pins the boundary.

## Decisions

- [ADR 0001 — the convex hull, not the chord](docs/adr/0001-hull-not-chord.md)
- [ADR 0002 — a deterministic oracle, and why it is not cheating](docs/adr/0002-deterministic-oracle.md)
- [ADR 0003 — recalibration as reparameterisation](docs/adr/0003-monotone-reparameterisation.md)
- [ADR 0004 — pricing the escalation](docs/adr/0004-value-of-information.md)
- [ADR 0005 — integer money and consecutive probes](docs/adr/0005-integer-money-and-probes.md)

## Longer write-ups

- [1 — What a routing result actually claims](docs/portfolio/01-what-a-routing-result-claims.md)
- [2 — The calibration result that says do nothing](docs/portfolio/02-the-calibration-non-result.md)
- [3 — Pricing the decision](docs/portfolio/03-pricing-the-decision.md)
- [4 — Four bugs the experiment found in my own design](docs/portfolio/04-bugs-the-experiment-found.md)
- [Known limitations](docs/known-limitations.md)
