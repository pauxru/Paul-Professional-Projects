# The plan that does not exist

Every migration engagement starts the same way. Somebody asks for the plan.

The expected deliverable is a sequence: wave one is these eight things, wave
two is these six, here is the date. The unspoken assumption is that a plan
exists and the job is to find a good one.

For this estate, that assumption is false, and demonstrating it is the most
valuable thing the model does.

## Where it breaks

An insurer's policy admin system has an online component and a nightly batch
component. They were built at the same time by the same team and they write
into the same schema. Not "the batch job calls an API on the online system" —
the same tables, the same grants, the same transactions.

There is no configuration in which one of those is in Azure and the other is
in a data centre in Slough. You cannot half-migrate a table.

So they are not two components that happen to be tightly coupled. They are one
migration unit, and it weighs 225 person-days. The wave capacity available to
the policy team is 100.

There is no valid plan.

The same is true twice more: a billing service fused to its database at 245
days against a capacity of 130, and a claims cluster at 190 against 120. Three
of nineteen migration units cannot be migrated at all, as the estate is
declared.

## Why an optimiser will not tell you this

The standard way to model an unsplittable dependency is a large penalty. Put a
big number on separating the two ends and the optimiser learns to keep them
together.

That approach always returns a plan. Under a penalty encoding, "impossible"
becomes "expensive", and expensive is a thing a steering committee knows how
to handle. They ask for the cheaper option. You produce one. It has a lower
penalty and it is still impossible.

The penalty weight is the problem. There is no correct value. Too low, and the
optimiser confidently schedules a cutover that cannot physically happen — and
it will look *attractive*, because the penalty is finite and everything else
about the plan is good. Too high, and the penalty term dominates every other
term in the objective, so licences, hybrid links and egress become rounding
error and the comparison between planning strategies stops meaning anything.

Somewhere in between there is a weight that produces a plan that looks
sensible. Nothing about it is more true than the others.

The model in this repository contracts instead. Union-find over the
unsplittable link kinds, run before planning starts. The planner never sees
the underlying components, only units, and a unit that exceeds every available
wave capacity produces an empty feasible set rather than an expensive plan.

## What you deliver instead

Refusing to produce a plan is only useful if you can say what would make one
possible.

The second output is that answer: the cheapest set of couplings to remove so
that a plan exists. Six edges, £183,000, 220 person-days of remediation. After
it, the estate contracts to 25 units and nothing exceeds capacity.

That is a deliverable a business can act on. It is a costed, scoped piece of
work with a clear success criterion — and critically, it belongs to a
different budget, a different team and probably a different quarter from the
migration itself. Programmes discover this in month nine. The model says it in
week one.

## The trap inside the answer

Finding the cheapest set of edges to break is where I wrote the same bug
twice, months apart.

The obvious approach: try breaking one edge, then two, then three, stopping at
the first size where some subset restores feasibility, and take the cheapest
subset of that size.

Feasibility is monotone in cardinality — if breaking `k` edges works, breaking
those `k` plus one more also works — so the search terminates and returns a
feasible answer. It just is not the cheapest one, because cost is not monotone
in cardinality. Two cheap cuts can beat one expensive one.

Both places in the model that searched subsets had this bug. Both docstrings
claimed optimality. Neither loop established it. On this estate the decoupling
answer happened to be unchanged — £183,000 either way — so no amount of
looking at the output would have found it. The claim was wrong, not the
number.

Both now enumerate all 1,024 and 8,192 subsets respectively, and the report
prints the counts so the exactness claim is backed by something visible.

## The greedy answer costs 76% more

There is a related trap that is not a bug, just a bad heuristic.

Break the cheapest coupling first. It is the natural move, and for the claims
cluster it costs £67,000 where the optimal answer costs £38,000 — 76% more for
the same feasibility.

The reason is specific and instructive: breaking `claims-web -> claims-db`
alone leaves the remaining unit at 135 person-days against a capacity of 120.
You paid for a cut that bought nothing. You then have to buy a second cut
anyway. Greedy pays twice because it optimises the price of the cut rather
than the feasibility the cut delivers.

## The general shape

A planning tool that always returns a plan is not a planning tool. It is a
plan generator, and the difference matters most on exactly the systems people
are most afraid to touch — because those are the systems where "we have a
plan" is the answer everybody most wants to hear.

The useful sequence is:

1. Encode physical impossibility as impossibility, not as a large number.
2. When there is no plan, say so, and say precisely which constraints bind.
3. Cost the smallest change that makes a plan exist.
4. Check that "smallest" is actually the objective you optimised, not one that
   correlates with it.

Step four is where I went wrong twice. It is easy to write a search whose
stopping rule belongs to the constraint rather than the objective, and the
resulting function returns a valid, plausible, wrong answer with a confident
docstring on top of it.
