# Migration Wave Planner -- what the arrangement is worth


## 0. The estate, and why it is written down rather than sampled

The subject is a UK general insurer's core estate: a policy administration
system with a batch tail, a claims stack, a finance ledger with its own
reporting warehouse, a customer portal, and the integration bus that
everything talks through. Six delivery teams, and every dependency between the
components written down by hand.

The estate is declared as data, not generated from a random graph. That is
deliberate and it is the same choice that makes the rest of the study
meaningful: the claims this project makes are all claims about *coupling* --
which things cannot move apart, which links survive a partial migration, which
team is the binding constraint. If the coupling were sampled, every result
would be a result about the sampler. A declared estate can be wrong about the
world, but it cannot be silently wrong about itself, and the invariants below
are checked at construction on every run.

| kind      | count |
|-----------|-------|
| app       | 8     |
| batch     | 4     |
| db        | 4     |
| fileshare | 2     |
| hub       | 2     |
| reporting | 2     |
| service   | 7     |

- 29 components, 54 dependencies, 6 teams
- total migration effort 1,610 person-days
- total per-wave capacity 745 person-days at a 2.5-month wave
- effort floor 9.00 months -- the duration if every team were perfectly packed
  with no dependencies at all
- horizon 48 months, freeze windows in months [2, 10, 11] of each year

> Two invariants fired during development and forced the estate to be retuned
> rather than the check to be relaxed: a component larger than its own team's
> per-wave capacity (unmovable by construction, which is a data error rather
> than a finding), and an unsplittable edge with no decoupling cost attached
> (which would have let the planner break a shared database for free).


## 1. The first answer is that the plan you asked for does not exist

Two components that share a database cannot cut over in different waves. Not
expensively -- at all. So before anything can be scheduled, every
shared-database edge is contracted: the components at its ends are merged into
a single migration unit that moves as one. Contraction is a union-find over
the unsplittable edges, with unions taken in sorted-id order so the resulting
unit identifiers are deterministic and this report hashes the same way every
run.

|             | components | units | external deps | infeasible units |
|-------------|------------|-------|---------------|------------------|
| as declared | 29         | 19    | 43            | 3                |

Three of those units are larger than the per-wave capacity of the team that
owns them. There is no schedule at all until that is fixed -- not a bad
schedule, no schedule:

| unit        | team    | needs (days) | team capacity | over by |
|-------------|---------|--------------|---------------|---------|
| pas-batch   | policy  | 225          | 100           | 125     |
| billing-svc | finance | 245          | 130           | 115     |
| claims-db   | claims  | 190          | 120           | 70      |

**Predicted.** The cheapest way to make the estate movable is to break the cheapest
decoupling edges -- pay the smallest invoices until the clusters fit.

The remediation problem is: choose a subset of breakable edges, minimising
cost, such that after contracting what remains, every unit fits in its team's
wave capacity. There are 1,024 candidate subsets and every one is evaluated,
so the answer below is optimal rather than merely good. Only 40 of them are
feasible at all, and the cheapest has 6 edges.

| broken edge               | cost         | effort (days) |
|---------------------------|--------------|---------------|
| pas-core -> pas-db        | £46,000      | 55            |
| claims-svc -> claims-db   | £38,000      | 45            |
| billing-web -> finance-db | £34,000      | 40            |
| pas-batch -> pas-db       | £24,000      | 30            |
| collections -> finance-db | £23,000      | 28            |
| ledger-feed -> finance-db | £18,000      | 22            |
| **total**                 | **£183,000** | **220**       |

**Found — prediction wrong.** The cheapest feasible remediation is not the cheapest set of edges. For the
claims cluster it breaks claims-svc -> claims-db at £38,000 and leaves
claims-web -> claims-db at £29,000 in place. Breaking the cheap one instead
leaves claims-db at 135 days against a capacity of 120, so a cheapest-first
strategy has to come back and break the expensive edge as well: £67,000 for
the same cluster, 76.3% more than the optimum. What is being bought is a
*shape*, and shapes do not have additive prices.

|                   | components | units | external deps | infeasible units |
|-------------------|------------|-------|---------------|------------------|
| after remediation | 29         | 25    | 50            | 0                |

The units that survive contraction, largest first:

| unit        | members                 | effort | criticality |
|-------------|-------------------------|--------|-------------|
| dw          | dw, etl                 | 155    | 2           |
| billing-svc | billing-svc, finance-db | 130    | 5           |
| claims-db   | claims-db, claims-web   | 120    | 4           |
| crm         | crm, crm-db             | 115    | 3           |
| esb         | esb                     | 110    | 5           |
| pas-core    | pas-core                | 95     | 5           |


## 2. What the arrangement is worth

With a movable estate, the question is the order. Four planners are compared,
all subject to the same capacity and dependency rules, all priced by the same
cost model.

**Predicted.** Clustering by coupling strength will do well: keeping chatty components
together is the whole point of a wave.

| planner              | waves | end month | 48-month cost |
|----------------------|-------|-----------|---------------|
| dependents_first     | 5     | 12.00     | £3,235,439    |
| least_critical_first | 5     | 12.00     | £3,218,717    |
| coupling_clusters    | 10    | 16.88     | £3,629,724    |
| annealed             | 5     | 12.00     | £2,967,337    |

**Found — prediction wrong.** Coupling-clustering is the worst planner in the set, at £3,629,724 across 10
waves. Grouping by coupling produces groups that do not fit the per-team
capacity, so the packer splits them anyway and pays wave overhead (£85,000 a
wave) for the privilege. The intuition is not wrong about coupling; it is
wrong about which constraint binds.

The spread looks modest on the total and is not modest at all. Most of the
total is floor: the cloud running cost that is paid whatever the order, plus
the remediation that feasibility already forced.

|                           | total      | floor      | discretionary |
|---------------------------|------------|------------|---------------|
| best (annealed)           | £2,967,337 | £1,991,640 | £975,697      |
| worst (coupling_clusters) | £3,629,724 | £1,991,640 | £1,638,084    |
| **spread**                | **22.3%**  | --         | **67.9%**     |

> Quoting the total is how a migration business case makes every option look
> similar. On the money an arrangement can actually move, the worst plan here is
> 67.9% worse than the best.

Against doing nothing -- £5,680,000 of on-prem running over 48 months -- the
annealed plan pays back in month 14.37, discounted.


## 3. "Best found" is a claim, and it needs a number attached

A heuristic that reports a cost is reporting the cost of whatever it happened
to find. Two things make that claim checkable: a lower bound valid for every
feasible plan, and exhaustive optimisation of sub-instances small enough to
enumerate.

| bound term    | value          | why it is a bound                                                                                                |
|---------------|----------------|------------------------------------------------------------------------------------------------------------------|
| wave overhead | £340,000       | at least 4 waves are needed to fit the estate into per-team capacity                                             |
| run cost      | £1,865,728     | each unit charged as if it moved at the earliest month its team could reach it, by the rearrangement inequality  |
| dual running  | £71,802        | each unit's own build time, ignoring queueing                                                                    |
| hybrid build  | £99,500        | per-unit knapsack over neighbours that could share a wave, halved because each split edge is agreed by both ends |
| remediation   | £183,000       | fixed by feasibility                                                                                             |
| **total**     | **£2,560,030** |                                                                                                                  |

Best found £2,967,337 against a bound of £2,560,030 is a gap of 15.91%. That
number is an upper bound on how much is left on the table, and the interesting
question is how much of it is search failure and how much is bound slack.

**Predicted.** The annealer is a heuristic on a combinatorial problem, so it will show a few
percent of true optimality gap on instances small enough to check.

| units | space     | feasible | exact optimum | annealer gap | best naive gap | bound slack |
|-------|-----------|----------|---------------|--------------|----------------|-------------|
| 6     | 15,625    | 3,422    | £1,219,613    | 0.000%       | 0.000%         | 6.47%       |
| 7     | 78,125    | 19,748   | £1,295,620    | 0.000%       | 0.000%         | 6.46%       |
| 8     | 390,625   | 122,572  | £1,431,720    | 0.000%       | 0.000%         | 5.94%       |
| 9     | 1,953,125 | 598,072  | £1,467,829    | 0.000%       | 0.000%         | 6.28%       |

**Found — prediction wrong.** The annealer reaches the exact optimum on every sub-instance that can be
checked, and the bound is loose by 5.94-6.47% on those same instances. So most
of the 15.91% headline gap is bound slack, not search failure. The honest
statement is not 'within 15.91% of optimal' -- it is 'within 15.91% of a bound
that is itself known to be several percent low'.

Why the calibration stops at 9 units: the enumerated space grows by about 5.0x
per unit added, so the full 25-unit instance is roughly 1.5e+11x larger than
the largest one solved exactly here. No constant factor closes that, which is
why the full instance gets a bound and a heuristic rather than a proof.

> The sub-instances are too easy to separate the planners -- the naive gap
> column is mostly zero as well. That is a limitation of the calibration, not a
> result: it says these sizes cannot distinguish search quality, so the solver
> comparison in section 2 has to come from the full instance where no exact
> answer exists. Reporting the calibration without this caveat would be the more
> flattering and less true option.

One provable piece of the slack: the bound charges nothing for licence waste,
and the best plan pays £63,382 of it. That term alone accounts for 15.6% of
the gap and can never be recovered by better search.

Search effort: 11,318 plan evaluations, 39 improvements to the incumbent,
1,034 uphill moves accepted, 3 restarts.


## 4. Where the money actually is

**Predicted.** Egress will be a material line. Data transfer out of the datacentre is the
cost everyone raises first in a migration review.

| term          | cost           | share of total |
|---------------|----------------|----------------|
| on-prem run   | £352,071       | 11.865%        |
| cloud run     | £1,613,536     | 54.377%        |
| dual running  | £88,807        | 2.993%         |
| licence waste | £63,382        | 2.136%         |
| hybrid build  | £186,000       | 6.268%         |
| hybrid run    | £55,329        | 1.865%         |
| egress        | £212           | 0.007%         |
| wave overhead | £425,000       | 14.323%        |
| remediation   | £183,000       | 6.167%         |
| **total**     | **£2,967,337** | 100%           |

**Found — prediction wrong.** Egress is £212, 0.007% of the programme. The hybrid links that carry that
traffic cost £241,329 to build and run -- 1136x the price of the bytes
crossing them. The expensive part of moving data between two places is not
moving the data. It is having two places.

- 50 dependencies cross unit boundaries and can become hybrid links, carrying
  2,614 GB/month
- wave overhead is £425,000 across 5 waves -- 2,000 times the egress bill, and
  the one term a planner controls directly by choosing how many boundaries to
  create
- dual running is £88,807: the estate pays for both sides only while a unit is
  being built


## 5. The renewal calendar is a planning variable

**Predicted.** Licence renewal dates are a rounding error next to running cost and wave
overhead; the optimiser will land wherever the capacity constraints push it.

| component    | annual  | renews (month) | cutover | waste       |
|--------------|---------|----------------|---------|-------------|
| pas-db       | £96,000 | 3              | 3.00    | £0          |
| finance-db   | £96,000 | 3              | 3.00    | £0          |
| dw           | £96,000 | 3              | 3.00    | £0          |
| esb          | £64,000 | 10             | 5.40    | £24,556     |
| crm          | £54,000 | 6              | 5.40    | £2,719      |
| pas-core     | £48,000 | 3              | 7.77    | £28,917     |
| claims-db    | £48,000 | 3              | 3.00    | £0          |
| crm-db       | £48,000 | 6              | 5.40    | £2,417      |
| bi           | £38,000 | 0              | 12.00   | £0          |
| quote-rating | £22,000 | 8              | 5.40    | £4,774      |
| **total**    |         |                |         | **£63,382** |

Whether the optimiser is genuinely exploiting those dates or merely landing on
them is a testable question, so it is tested: run the same search with the
licence term hidden from the objective, then charge the resulting plan the
real bill.

| search        | licence waste | everything else | total      |
|---------------|---------------|-----------------|------------|
| renewal-aware | £63,382       | £2,903,955      | £2,967,337 |
| renewal-blind | £299,417      | £2,841,286      | £3,140,702 |

**Found — prediction wrong.** Hiding renewal dates from the search costs £173,365, or 17.8% of the
discretionary spend. The blind plan is actually £62,669 *better* on every
other term -- it optimised harder on what it could see -- and still loses,
because it stranded £236,035 of unexpired licence term. A contract renewal
date is not procurement trivia; it is a constraint with a price, and it
appears on no architecture diagram.

> 5 components (pas-db, finance-db, dw, claims-db, bi) waste nothing because the
> plan cuts them over in their renewal month. That is the optimiser finding an
> alignment rather than a coincidence: the renewal-blind run above strands 4 of
> them.


## 6. The date on the plan is not the date

**Predicted.** The deterministic end date is optimistic, because a wave ends when its slowest
team ends and the mean of a maximum exceeds the maximum of the means.

| statistic                         | month |
|-----------------------------------|-------|
| deterministic plan                | 12.00 |
| P50                               | 12.74 |
| P80                               | 15.00 |
| P90                               | 15.75 |
| P95                               | 16.54 |
| mean                              | 12.43 |
| merge bias (mean - deterministic) | +0.43 |
| median slip (P50 - deterministic) | +0.74 |

**Found.** The plan says month 12.00. The mean outcome is 12.43 and the P90 is 15.75 --
31.3% past the date on the slide. Per-task estimates are unbiased by
construction here (the log-normal is median-corrected so each team's expected
effort equals its point estimate), so the +0.43 months is not padding coming
back out of the estimates: it is the cost of joining parallel work at wave
boundaries.

Priced: a P90 landing costs about £3,331,242 against the deterministic
£2,967,337, +12.3% more, because everything on-prem keeps running while the
programme runs late.

**Predicted.** The median slip and the mean slip measure the same thing and will roughly
agree, on every plan.

| plan                 | plan date | mean slip | median slip | heaviest single outcome | median on it |
|----------------------|-----------|-----------|-------------|-------------------------|--------------|
| annealed             | 12.00     | +0.43     | +0.74       | 12.00 (20.2%)           | no           |
| coupling_clusters    | 16.88     | +1.38     | +0.48       | 15.00 (6.6%)            | no           |
| dependents_first     | 12.00     | +0.84     | +0.00       | 12.00 (30.4%)           | yes          |
| least_critical_first | 12.00     | +1.17     | +1.21       | 12.00 (21.9%)           | no           |

**Found — prediction wrong.** They do not agree, and the disagreement is structural rather than noise.
Freeze months are [2, 10, 11], so every draw whose unconstrained end lands
anywhere inside a run of frozen months is pushed to the same date: the outcome
distribution is mixed, not continuous, and up to 30.4% of all outcomes pile
onto a single month. On the dependents_first plan it happens: 30.4% of
outcomes land on exactly month 12.00, the median lands inside that atom, and
the median slip reads +0.00 months while the mean slip is +0.84.

The direction is not fixed either. With freezes off the mean slip on the best
plan is +1.39 months against a plan date of 9.90; with freezes on it is +0.43
against 12.00. Freezes make the *reported* bias smaller while making the
actual date later, because the deterministic plan has already absorbed the
slippage that the simulation would otherwise have discovered. Any programme
quoting a P50 against a freeze calendar is quoting a quantised statistic; the
mean does not have this failure mode, which is why it is the headline number
above.


## 7. Merge bias comes from balance, not from headcount

**Predicted.** Merge bias grows with the number of teams in a wave: more parallel streams,
more chances for one to be late.

| wave | teams | near-critical streams | longest (months) | bias   |
|------|-------|-----------------------|------------------|--------|
| 1    | 5     | 5                     | 2.50             | +0.932 |
| 2    | 6     | 3                     | 2.40             | +0.733 |
| 3    | 5     | 2                     | 2.38             | +0.385 |
| 4    | 1     | 1                     | 1.50             | +0.000 |
| 5    | 2     | 1                     | 1.12             | +0.025 |

**Found — prediction wrong.** Team count explains the bias poorly (rank correlation 0.821): waves 1 and 3
both run 5 teams and differ by 2.4x in bias. What predicts it is the number of
*near-critical* streams -- teams whose deterministic duration is within 20% of
the wave's longest, and so could plausibly finish last (rank correlation
0.975). A wave with 5 teams and one long pole behaves like a wave with one
team.

The obvious next claim is that cost optimisation manufactures this risk:
balancing waves is exactly what a cost optimiser does, since idle team
capacity is wasted wave overhead, and balance is what produces near-critical
streams. It is worth measuring before it is said.

**Predicted.** The cheapest plan will carry the most merge bias, because it is the most
balanced.

| planner              | cost       | deterministic end | P90   | merge bias | near-critical streams (all waves) |
|----------------------|------------|-------------------|-------|------------|-----------------------------------|
| dependents_first     | £3,235,439 | 10.42             | 14.99 | +1.264     | 13                                |
| least_critical_first | £3,218,717 | 10.47             | 15.38 | +1.528     | 13                                |
| annealed             | £2,967,337 | 9.90              | 14.51 | +1.394     | 12                                |

**Found — prediction wrong.** It does not hold. The cheapest plan (annealed, +1.394) sits between the other
two, and the most biased is least_critical_first at +1.528 -- the dearest plan
but one. The whole spread is 0.264 months, against a Monte Carlo standard
error of 0.0189, so it is a real ordering and not noise; it simply is not the
ordering the argument predicted. Section 2's cost differences between these
plans come mostly from wave count and cutover timing, which the balance story
does not touch.

> Measured with freezes disabled so the comparison is not an artefact of
> month-boundary pinning: with freezes on, one of these plans reports a merge
> bias of exactly zero because every draw lands on the same post-freeze month.
> The mechanism in the first half of this section is sound and the extrapolation
> to 'optimisers create risk' is not, which is the more useful half of the
> result: a mechanism that is real at the level of one wave does not
> automatically aggregate to a claim about whole plans.


## 8. The correlation assumption outweighs the estimates

Effort overruns are modelled with a two-level Gaussian copula: a
programme-wide factor (weight a) that makes everything late together, and a
per-wave factor (weight b) that makes one wave late together. Both default to
plausible values that nobody in a real programme ever writes down.

**Predicted.** Both knobs are forms of 'things go wrong together', so raising either one will
lengthen the tail; the only question is by how much.

| a (programme) | b (wave) | P50   | P90   | sd    | merge bias |
|---------------|----------|-------|-------|-------|------------|
| 0.00          | 0.00     | 11.83 | 14.07 | 1.595 | +2.087     |
| 0.00          | 0.45     | 11.18 | 13.59 | 1.714 | +1.421     |
| 0.45          | 0.00     | 10.93 | 15.15 | 2.944 | +1.390     |
| 0.25          | 0.20     | 11.03 | 14.55 | 2.458 | +1.398     |
| 0.60          | 0.30     | 9.92  | 14.32 | 3.040 | +0.433     |

> Swept with change freezes disabled. Freeze slippage snaps the end date onto
> month boundaries, which hides differences between correlation settings behind
> a quantisation artefact -- one earlier run of this sweep reported a merge bias
> of exactly zero for a setting where the bias is plainly not zero, purely
> because every draw landed on the same post-freeze month.

**Found — prediction wrong.** P90 ranges from 13.59 to 15.15 months -- a 1.57-month spread -- across
assumptions that are all defensible and none of which is usually stated. The
two levels push in opposite directions: within-wave correlation *shortens* the
tail, because when teams in a wave move together the maximum stops being a
maximum of independent draws, while programme-level correlation lengthens it.
A model with one correlation knob cannot represent this and will be wrong in a
direction that depends on which effect it happened to capture.


## 9. Change freezes are a cost line, not a hedge

Cutovers are forbidden in months [2, 10, 11] -- year end and the renewal peak.
A cutover landing in a freeze slips to the next open month.

**Predicted.** Freezes cost calendar time but compress the distribution, because slipping to
a fixed boundary absorbs variation.

|                  | deterministic | P50   | P90   | sd    |
|------------------|---------------|-------|-------|-------|
| freezes enforced | 12.00         | 12.74 | 15.75 | 2.675 |
| freezes ignored  | 9.90          | 11.05 | 14.51 | 2.435 |

**Found — prediction wrong.** Freezes cost 2.10 months of deterministic date (21.3%) and the standard
deviation rises from 2.435 to 2.675. The absorbing effect is real for an
individual cutover but does not survive to the programme end date, because a
wave that slips past a boundary carries its slip into every later wave. Freeze
windows are a cost, not a hedge.

> The absorbing barrier does show up locally: it is why the deterministic end
> lands on a round month, and why a plan whose last wave happens to be pinned by
> a freeze can report an artificially small merge bias. That is why section 7's
> cross-planner comparison is run with freezes disabled.


## 10. Blast radius, and the trade-off nobody prices

A wave's exposure is every component that transitively depends on something
moving in it, weighted by criticality. Live links are dependencies with one
end migrated and the other not -- the things that break at three in the
morning.

| wave | moving | exposed components | weighted exposure | live links |
|------|--------|--------------------|-------------------|------------|
| 1    | 11     | 23                 | 92                | 24         |
| 2    | 9      | 13                 | 54                | 13         |
| 3    | 6      | 11                 | 49                | 7          |
| 4    | 1      | 1                  | 5                 | 4          |
| 5    | 2      | 2                  | 6                 | 0          |

**Predicted.** The cheapest plan will also be the least exposed: fewer waves means fewer
boundaries means fewer live links.

| planner              | cost       | waves | peak weighted exposure | peak live links |
|----------------------|------------|-------|------------------------|-----------------|
| annealed             | £2,967,337 | 5     | 92                     | 24              |
| least_critical_first | £3,218,717 | 5     | 72                     | 25              |
| dependents_first     | £3,235,439 | 5     | 73                     | 24              |
| coupling_clusters    | £3,629,724 | 10    | 63                     | 22              |

**Found — prediction wrong.** The cheapest plan (annealed) has the *highest* peak weighted exposure in the
set: 92 against 63-73 for the rest. On live links it is mid-pack -- 24 against
22-25. It packs a large, well-connected first wave: many components exposed at
once, but not an unusually large hybrid boundary left behind. The two measures
rank the plans differently, and a programme that tracks only one of them is
choosing an answer rather than measuring one.

3 of 4 plans are on the cost/exposure frontier: annealed (£2,967,337, 92),
least_critical_first (£3,218,717, 72), coupling_clusters (£3,629,724, 63).


## 11. What to do with this

- Spend the £183,000 on decoupling first, and spend it on the exact set above
  rather than the cheapest invoices -- the estate is not schedulable until it
  is spent, and greedy selection overpays.
- Get the licence renewal calendar before the architecture review, not after:
  it is worth £173,365 here, more than any architectural choice in the plan.
- Quote 15.0-15.8 months, not 12.00. If a single date is required, use the P80
  and say which one it is.
- State the correlation assumption in the risk section. It is worth 1.57
  months of P90 -- more than most of the estimates it is applied to.
- Track both exposure measures. They rank the plans differently, and the
  disagreement is the actual decision.
- Treat the change freeze as a cost line. It buys 2.10 months of delay and no
  variance reduction; if it is a policy, price it as one.


## Reproducing this

```text
python run_planner.py            # regenerate this file
python -m pytest tests -q        # the test suite
./test.ps1                       # tests + byte-identical rebuild
```

Every figure above is computed by `run_planner.py` on the declared estate.
Solvers, the annealer and the Monte Carlo are all seeded, so this file is
byte-identical on every run; `test.ps1` regenerates it and compares hashes,
which is how a transcription error or an accidental dependence on dictionary
ordering gets caught.


---

12 predictions were written before the corresponding measurement was read. 1 held; 11 did not, and each of those is discussed where it appears.
