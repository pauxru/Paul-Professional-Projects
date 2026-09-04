# 2. A shared schema is a constraint, not a cost

**Status:** accepted

## Context

Two components that write the same database schema cannot cut over at
different times. There is no configuration where half a table is in Azure.

There are two ways to encode that. The soft one gives such an edge a very
large hybrid cost, so the optimiser learns to keep both ends together. The
hard one contracts the two components into a single indivisible unit before
planning starts, so no plan that separates them can be constructed at all.

The soft encoding is easier. It needs no new data structure, it keeps the
search space uniform, and it usually produces the same answer.

## Decision

Contract. `wave/units.py` runs union-find over the unsplittable link kinds
(`shared_db`, `replica`) and produces migration units. The planner never sees
the underlying components.

## Consequences

**The model can now say "no".** Contracting 29 components yields 19 units,
three of which exceed every wave capacity available to them: 225 against 100,
245 against 130, 190 against 120. Under the soft encoding those become
expensive plans. Under the hard encoding they become *no plan*, and the
planner's first output is the remediation programme required to make planning
possible — £183,000 across six edges and 220 person-days.

That is the single most valuable output in the project, and the soft encoding
cannot produce it. A planner that always returns a plan is a planner that
cannot tell you the plan does not exist, and "we have a plan" is exactly the
answer a steering committee wants to hear about a system nobody wants to
touch.

**Penalty weights are a lie with a number attached.** Under the soft encoding
the answer depends on the penalty. Set it too low and the optimiser cheerfully
schedules a cutover that cannot physically happen, and it will look
*attractive* because the penalty is finite. Set it high and it dominates the
objective, so every other term — licences, hybrid links, egress — is rounding
error and the comparison between planners becomes meaningless. There is no
correct value, because the quantity being priced is not a cost.

**Feasibility becomes a property, not a score.** With the constraint hard,
`infeasible_units` is a list, `cheapest_feasible_decoupling` returns an exact
answer over the lattice of remediable edges, and the tests can assert
optimality by brute force. Under the soft encoding all three become
threshold questions.

**What it costs.** The search space shrinks in a way that is not free: some
genuinely good plans are unreachable if the contraction is wrong. If an edge
is marked `shared_db` when in practice it is a read-only view that could be
replicated, the planner will demand remediation nobody needs. The estate's
link taxonomy is therefore load-bearing, and `docs/known-limitations.md` names
it as the assumption most likely to be wrong in a real engagement.

**A second-order effect worth naming.** Contraction makes the unit efforts
lumpy — 225 days in one unit next to 40 in another — which is what makes wave
packing hard and what makes the difference between heuristics visible in
section 2. Under the soft encoding the instance is smoother and all four
planners land within a couple of percent of each other, which would have made
the whole solver comparison look like noise.

## Alternatives considered

**Contract, but allow the contraction to be broken at a price.** This is what
the model actually does, and it is the reason the decision is defensible.
`cheapest_feasible_decoupling` enumerates all 2^10 subsets of remediable edges
and returns the cheapest subset that restores feasibility. So the hard
constraint is not a refusal to think — it is a refusal to *pretend*, followed
by a costed answer to "what would it take". The two-phase shape is the point:
remediation is a different budget, a different team and a different quarter
from migration, and mixing them into one objective is how programmes end up
discovering in month nine that the plan required work nobody scheduled.
