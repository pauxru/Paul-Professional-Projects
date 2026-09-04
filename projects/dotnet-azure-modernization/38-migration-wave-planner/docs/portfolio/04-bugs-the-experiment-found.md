# Bugs the experiment found

The model was written first, then the report, then the tests. That order was
not planned but it turned out to be instructive: writing tests against a
report that already existed found seven defects the report had been quietly
reporting around, and two of them changed published numbers.

Each of these is a class of mistake, not a typo.

---

## 1 and 2. Largest feasible is not cheapest feasible

**Where:** `cheapest_feasible_decoupling`, and independently
`_hybrid_build_bound`.

Both search a space of subsets for a cheapest feasible one. Both were written
the same way: enumerate by cardinality, stop at the first size where something
feasible appears, take the cheapest subset of that size. Both docstrings
claimed to return the cheapest feasible answer.

Feasibility is monotone in cardinality, so the search terminates and returns
something valid. Cost is not monotone in cardinality, so what it returns is
not the cheapest. Two cheap cuts can beat one expensive cut.

**Why it survived:** the answer is plausible. It is feasible, it is a
reasonable size, and the cost looks sensible. On this estate the decoupling
answer was unchanged — £183,000, six edges, 40 of 1,024 subsets feasible — so
the bug had been shipping a correct number with an incorrect justification.
Nothing downstream could detect it.

**Found by:** writing a test that brute-forced the same question two
independent ways and compared. The bound version was found by looking for the
same pattern after the first one turned up.

**The general lesson:** when a search stops early, the stopping rule has to be
a property of the *objective*, not of the constraint. The tell is a docstring
using a superlative the loop does not establish. I wrote this bug twice,
months apart, because the layered shape *feels* like a branch-and-bound with
an obvious bound — and there is no bound involved.

---

## 3. A memoised closure whose value depended on the call stack

**Where:** `_dependents` in `wave/blast.py`.

Blast radius needs the transitive closure of "who breaks if this breaks" over
propagating dependency links. The estate has cycles in it — a message hub
everything talks to, a warehouse feeding a report feeding an operational
screen — so the naive recursion does not terminate.

The implementation carried a stack of nodes currently being visited and
skipped any node already on it, then memoised the result under the node.

The memo key was the node. The memo *value* depended on the stack. So a node
first reached inside a cycle got a truncated answer cached under it, and every
later caller got the truncation.

**The symptom:** the blast radius of a component depended on the order
components appear in a source file. Reversing `estate.components` changed the
answer.

**Found by:** a test that added a deliberate two-node cycle and asserted each
side was exposed to the other. One direction passed and the other failed,
because the outer loop had already visited one of them.

**The fix:** a plain search per node, no cross-node memoisation. Twenty-nine
nodes and fifty-four edges — the memoisation was buying nothing and costing
correctness.

**The general lesson:** memoising a function whose result depends on ambient
context is only safe if the context is in the key. Cycle-breaking state is
exactly such a context, and it is invisible at the call site.

---

## 4. Tied points annihilated each other on the Pareto frontier

**Where:** `pareto` in `wave/blast.py`.

The dominance test was `q[0] <= p[0] and q[1] <= p[1] and q != p`. Two plans
with identical cost and identical exposure each satisfy that test against the
other, so **both are discarded** and the frontier silently loses a point that
belongs on it.

There was even a tie-handling line further down the function that could never
execute, because both tied points had already been filtered out.

**Why it matters here specifically:** the points on this frontier are plans
produced by different heuristics over the same estate. Heuristics agreeing is
the normal case, not the odd one.

**Found by:** a two-line test asserting that `pareto([(1,1,'a'), (1,1,'b')])`
has one element. It returned zero.

**The fix:** strict dominance — no worse on both axes *and* strictly better on
at least one — then deduplicate by coordinate with a deterministic tie-break.

---

## 5. The reported statistic could read zero on a plan that was months late

**Where:** `RiskResult.merge_bias`.

Defined as `P50 - deterministic`. A test asserted it was positive. It returned
exactly `0.0`.

Change freezes cover months 2, 10 and 11. Ten and eleven are adjacent, so
every draw landing anywhere in a two-month window is pushed to exactly month
12. The outcome distribution is mixed rather than continuous and carries an
atom of probability as wide as the freeze. On one of the four plans that atom
holds 30.4% of all outcomes, and the median falls inside it.

`P50 - deterministic` reads **+0.00 months**. `mean - deterministic` reads
**+0.84**.

**The fix:** `merge_bias` is now mean-based — which is also the statistic that
matches the claim being made, `E[max] > max[E]` — with `median_slip` reported
alongside it, plus `largest_atom()` and `median_in_atom()` so the report can
show the quantisation rather than assert it.

**Why this is the most valuable one:** it is not really a bug in the code. It
is a bug in a *reporting convention* that is used across enterprise delivery.
Any programme quoting a P50 against a quantised delivery constraint — freeze
calendars, quarterly release trains, monthly regulatory windows — is quoting a
statistic that can read zero slip while the distribution is months late.

---

## 6. The report had a wall-clock column in it

**Where:** the exact-calibration table in section 3.

`docs/results.md` is byte-compared between runs as a reproducibility check.
The check failed on the second run with a diff of `6.3s` against `6.1s`.

**The fix:** delete the column, and replace the argument it supported —
"exact solving does not scale to the full instance" — with the growth factor
of the enumerated search space, which is deterministic and machine-independent
and a better argument anyway.

**The general lesson:** if an artefact is checked for reproducibility, wall
clock time cannot appear in it. The reproducibility check is worth more than
the timing, and the timing usually has a deterministic substitute that makes a
stronger claim.

---

## 7. Names that said the opposite of what the code did

Three of these, found by reading the code while writing tests for it:

- `_slip_out_of_freeze` returned the month a cutover *landed on*, not the slip.
  Every caller was correct; every reader was misled. Renamed `_out_of_freeze`.
- `criticality_first` scheduled the *least* critical units first. The docstring
  was right and the name was wrong, which is the worse combination — a reader
  who trusts names and skims docstrings gets the opposite of the truth.
  Renamed `least_critical_first`.
- `dependency_order` scheduled deepest-first, which is not what "dependency
  order" means to most readers. Renamed `dependents_first`.

Also in this category: `CostBreakdown.controllable`, a field that summed terms
which were not in fact controllable by the planner. Deleted and replaced with
`cost_floor()`, which computes what no plan can beat — the quantity the field
had been pretending to be the complement of.

---

## What the report itself caught

Separately from the tests, the report generator's expect/found discipline
caught five factual errors in prose. `wave/report.py` raises if `found()` is
called without a preceding `expect()`, and raises again if a prediction is
left open at render time. Every number in the report is a formatted computed
value; none is a literal.

The errors were all the same shape — a sentence that was true when written and
stopped being true when the data moved:

- "waves 1 and 2 both run five teams" — it became waves 1 and 3
- "fifty-five dependencies" — there are fifty-four
- "the fewest peak live links" — it was mid-pack
- "strands all three" — it was four of five
- an entire paragraph explaining an atom of probability at the median, on a
  run where the median was not in an atom and the measured mass was 0.0%

That last one is the best argument for the discipline. The prose was correct
*reasoning*, applied to a plan where the phenomenon did not occur. Fixing it
meant measuring the atom across all four plans, which is what turned a vague
caveat into the finding in section 6.

Eleven of twelve predictions in the final report are recorded as contradicted.
That number is the output of the method, not an admission.
