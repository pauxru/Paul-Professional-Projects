# What a migration business case hides

The business case for moving an insurer's estate to Azure has a shape everyone
recognises. On-premises run cost goes away. Cloud run cost appears. Migration
effort is a one-off. Somewhere near the bottom there is a line about egress
charges, usually with a note that it needs watching.

Modelling it properly moves almost all the money somewhere else.

## Egress is 0.007% of the programme

The estate has 50 dependencies that cross a wave boundary at some point,
carrying 2,614 GB a month between components on opposite sides of the
migration. At £0.070 per GB, the total egress bill across the entire programme
is **£212**.

The programme costs £2,967,337. Egress is seven thousandths of one percent of
it. No plausible refinement of the egress rate changes any decision. It could
be off by a factor of fifty and still not matter.

Meanwhile, the hybrid links that carry those bytes cost **£241,329** to build
and run. Standing up a temporary connection between a migrated component and
one that has not moved yet means a VPN route, a firewall change, a shim on one
side or both, testing of both paths, and then a monthly cost for the
ExpressRoute share, the monitoring, and the on-call attention that hybrid
links always attract.

**The connection costs 1,136 times the value of the bytes crossing it.**

That ratio is the useful output. Not the £212, and not really the £241,329
either — the observation that the migration conversation has been anchored on
the wrong term by a factor of a thousand, and that the actual cost of being
half-migrated is the cost of *being connected*, not the cost of what flows
over the connection.

Add £425,000 of per-wave overhead — cutover weekends, change board,
comms, the standing cost of running a wave at all — and the "cost of
being half-migrated" reaches £666,000. That is 22% of the programme, against
an egress line the business case would have highlighted.

## The renewal calendar is a planning variable

Here is a cost that does not appear on any migration business case I have
seen.

A component running on-premises has software licences attached — database
licences, middleware, monitoring agents. Those renew annually, on a date. When
the component migrates, the on-premises licence stops being useful. If it was
renewed the month before, the organisation has paid for twelve months and used
one.

This is not a rounding error, and it is not hard to avoid. It is a scheduling
constraint that nobody encodes, because licence renewals live in procurement
and migration waves live in delivery, and the two systems do not talk.

I tested it by building a second objective function identical to the first
except that it charges nothing for licence waste, optimising against it, and
then pricing the resulting plan honestly.

The renewal-blind plan is **better on every other term**. It has lower hybrid
costs, lower overhead, a shorter dual-run period — £62,669 better in total
across everything except licences.

It wastes **£299,417** in stranded licences against the renewal-aware plan's
£63,382.

Net: the blind plan loses by **£173,365**, which is 17.8% of the programme's
entire discretionary spend. Five components in this estate have a renewal date
that falls within a month of a plausible cutover; the blind planner strands
four of them.

The mechanism is worth stating plainly, because it is not about the size of
the licence estate. A cutover one month *after* a renewal wastes eleven months
of licence. The same cutover one month *before* wastes nothing. The migration
work is identical. Nobody is looking at the calendar that decides which one
happens.

## Discretionary spend is the denominator that matters

Four planning strategies produce plans ranging from £2,967,337 to £3,629,724 —
a spread of 22.3%. That sounds like a modest difference in a large programme.

But most of that £2.97m is not discretionary. The lower bound — the cost no
valid plan can beat, given the estate, the capacities and the remediation that
has to happen — is **£1,991,640**. It is the migration effort itself, the
minimum run cost, the unavoidable hybrid links.

Against discretionary spend, the four planners differ by **67.9%**.

That is the number a sponsor should hear. "Planning could save you 22%" gets
filed under optimisation. "Two thirds of the money you can actually influence
is on the table" is a different conversation, and it is the more accurate one.

## The comparison nobody makes

Do nothing costs **£5,680,000** over the 48-month horizon — the estate keeps
running on premises, the licences keep renewing, the hardware keeps ageing.

Against the best plan, the programme pays for itself in **month 14.37**.

That number is only meaningful because everything above it is modelled: the
hybrid links, the licence timing, the wave overhead, the dual-run period where
both estates are live. A business case that models the migration effort and
the run-cost delta and calls it done will produce a payback figure that is
optimistic by however much of the £666,000 half-migration cost it left out.

## What to take from this

Three questions worth asking about any cloud business case:

1. **What is the cost of being half-migrated, as a line item?** If the answer
   is "egress", the model has not been built. The dominant term is almost
   always the temporary connective tissue and the standing cost of running
   waves.

2. **Which calendars is the plan blind to?** Licence renewals here; elsewhere
   it is support contract expiry, hardware lease end, regulatory reporting
   windows, or the fiscal year. These are cheap to encode and expensive to
   ignore, and they are invisible because they belong to a different
   department.

3. **What is the floor?** A percentage saving against total programme cost
   understates the value of planning by however much of the programme is
   fixed. Compute the cost no plan can beat and quote the spread against the
   difference.
