# Known limitations

Written to be read by someone deciding whether to trust a number from this
model, not to pre-empt criticism.

## The estate is one instance

Every figure in `docs/results.md` is a statement about a specific declared
estate of 29 components. The mechanisms generalise — cost is not monotone in
cut cardinality, a maximum of correlated log-normals is biased upward,
quantised delivery constraints break medians — but the magnitudes do not.

"Egress is 0.007% of programme cost" is true here because this estate has
2,614 GB/month of cross-boundary traffic and £425,000 of hybrid overhead. An
estate with a chatty analytics workload moving terabytes would land somewhere
else entirely. The transferable claim is the *ratio* being examined at all:
the hybrid links cost 1,136x the value of the bytes crossing them, and that
ratio is worth measuring on any estate before the cloud conversation defaults
to egress.

The missing work is a generator producing families of estates with controlled
structure, so the findings could be reported as distributions rather than
points. See ADR 0001 for why that was cut rather than done badly.

## Sigma is assumed, not measured

`SIGMA = 0.35` is a plausible coefficient of variation for software effort
estimation. It is not this organisation's historical estimation error, because
there is no such data here.

It is the largest single lever in section 6. The merge bias scales roughly
with `sigma^2`, so halving sigma roughly quarters the reported bias. Any
organisation using this model should replace it with their own delivery
history before quoting a P90, and if they do not have that history, the
correct output is a sensitivity range rather than a number.

The same applies to `RHO_PROGRAMME = 0.25` and `RHO_WAVE = 0.20`. Section 8
exists specifically to show how much those two assumptions are worth: 1.57
months of P90 spread across a plausible range, which is larger than several of
the effects the model is used to compare. They are the least evidenced numbers
in the model and among the most consequential.

## Decoupled edges are priced at zero hybrid cost

An estate invariant requires that unsplittable links carry no hybrid cost —
there is no configuration where half a table is in Azure, so there is no
hybrid link to price. That invariant is right for the *pre-remediation*
estate.

It is wrong afterwards. Once £183,000 has been spent replacing a shared schema
with an API, the two components can be separated — and that API is a real
cross-boundary call with real latency and real cost while the two ends are in
different places. The model charges nothing for it.

Concretely: `coupling_weight` returns 0 for 5 of the 47 unit pairs it is asked
about, and those five are exactly the pairs whose coupling the programme just
paid to remove. The effect is that the planner slightly under-values keeping
remediated pairs together. `tests/test_solvers.py` pins the behaviour
(`test_the_broken_dependencies_themselves_weigh_nothing`) so it cannot change
silently.

The fix is a `post_decouple_monthly` field on `Dependency` populated during
remediation planning. It was not done because inventing a price for an API
that does not exist yet is a modelling decision with no anchor, and a wrong
number embedded in the objective is worse than a visible zero.

## The sub-instances are too easy to separate the planners

Section 3's exact calibration shows the annealer at 0.000% gap on every
sub-instance. It also shows the best naive heuristic at 0.000%. The
sub-instances of 6–9 units cannot distinguish search quality at all.

So the calibration establishes that the *bound* is loose by about 6%, which is
what it is used for. It does not establish that the annealer is good. The
evidence for that is only the full-instance comparison in section 2, where
there is no exact answer to check against. Reported in a note directly under
the table.

## The freeze model is coarse

A cutover landing in a freeze month slips to the first non-frozen month
boundary. Real change freezes have exceptions, emergency change processes,
and partial windows — and real cutovers are scheduled weeks in advance rather
than landing wherever the arithmetic puts them.

This coarseness is why the outcome distribution has atoms in it, and the atoms
are the subject of a genuine finding (section 6). But the finding is about the
*existence* of quantisation, which is real, not about the specific 30.4% mass
on month 12, which is an artefact of modelling freezes as hard month
boundaries.

## Blast radius is structural, not probabilistic

`wave/blast.py` counts who *could* be affected: the transitive closure over
propagating links, weighted by declared criticality. It says nothing about how
likely anything is to break, or for how long.

A wave with 92 weighted exposure is not "twice as risky" as one with 46. It
has twice as much criticality-weighted surface area. Turning that into an
expected-loss number needs per-component failure rates and impact costs, and
inventing those would produce a number with a currency symbol and no content.

## Egress pricing is a single flat rate

`EGRESS_PER_GB = 0.070`. Real cloud egress is tiered, varies by destination
and region, and is negotiable at this scale. Given that egress lands at £212
across the entire programme, no plausible refinement of this number changes
any conclusion — which is itself the finding in section 4.

## Cost is modelled, not audited

Team rates, licence renewal values, hybrid build costs and on-premises run
costs are all declared. In a real engagement each of these is a negotiation
with a different part of finance, and the numbers move. The model's structure
survives that; the magnitudes do not.

The one place where this matters most is the licence finding in section 5. The
17.8% penalty for renewal-blind planning depends on the renewal *values* being
material relative to the migration cost. Halve the licence estate and the
finding weakens; the mechanism — that a cutover one month after a renewal
strands twelve months of paid-for licence — does not.

## Not modelled at all

- Team availability, holidays, attrition, and the ramp-up cost of a team
  joining a wave it has not worked on
- Data migration time and cutover windows — a 900 GB database does not move
  instantaneously
- Rollback, and the cost of a failed cutover
- Application remediation for cloud compatibility beyond the decoupling work
- Any dependency on external parties, regulators or third-party vendors
- Benefits realisation beyond the avoided on-premises run cost
