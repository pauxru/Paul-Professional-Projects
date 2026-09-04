# 1. The estate is declared, not sampled

**Status:** accepted

## Context

The model needs an estate to plan over. Two options: generate one from a
random graph model with tunable parameters, or write one down.

Generation is more defensible-looking. It gives you a family of instances, it
supports sensitivity analysis, and it lets you claim results hold "in general"
rather than on one example.

## Decision

Write the estate down. Twenty-nine named components with real roles — a policy
admin core, a claims database, a message hub, a warehouse — and fifty-four
named dependencies with declared link kinds.

## Consequences

**What this buys.** The findings that matter in this project are all about
*structure that a random graph does not have*:

- `pas-batch` writes into the same schema `pas-core` reads. That is not an
  edge with a weight; it is a design decision made in 2004 that makes the two
  components a single migration unit weighing 225 person-days against a wave
  capacity of 100. A random graph produces edges. It does not produce the
  specific pathology that a nightly batch job and an online system share a
  schema, which is the reason the plan does not exist.
- Three of the six licence renewals fall in the second half of the year. That
  is why the renewal-blind experiment in section 5 has something to find. Under
  a uniform random renewal calendar the effect exists but is smaller and
  less interpretable, and the finding stops being "the renewal calendar is a
  planning variable" and becomes "there is a term in the objective".
- The freeze calendar is months 2, 10 and 11 — year-end and the January
  renewal peak. Two of those are adjacent, which is why the atom of
  probability in section 6 is two months wide and heavy enough to swallow a
  median. A random freeze calendar would rarely produce adjacent freezes and
  the effect would look like noise.

**What it costs.** Every number in `docs/results.md` is a statement about this
estate. The generalisation is in the *mechanisms* — cost is not monotone in
cut cardinality, a maximum of correlated log-normals is biased upward,
quantisation makes medians unreliable — not in the magnitudes. The report says
so, and `docs/known-limitations.md` says so more bluntly.

**How the estate is defended.** A declared estate can be quietly wrong in a
way a generated one cannot: a typo produces an estate that is subtly
impossible rather than one that fails to build. `wave/estate.py` therefore
runs thirteen invariants at import:

- every dependency references components that exist
- no component depends on itself
- effort splits sum to the component total
- every team named in an effort split has a rate
- unsplittable links carry no hybrid cost, because there is no hybrid
  configuration for a shared schema — you cannot half-migrate a table
- decoupled edges carry a remediation cost and effort, and only unsplittable
  edges do
- criticality is in range, wave capacities are positive, licence renewals are
  inside the horizon

Each invariant has a matching negative test in `tests/test_estate.py` that
mutates the estate to violate it and asserts the build refuses. An invariant
that has never been seen to fire is not known to work.

## Alternatives considered

**Generate and validate.** Generate random estates, keep the ones that satisfy
the invariants. Rejected because the invariants are cheap to satisfy and the
*structure* is the expensive part — the acceptance rate for "has a shared
schema straddling a capacity boundary" is low enough that you end up
hand-tuning the generator until it produces the estate you would have written
down, with an extra layer of indirection between you and the assumption.

**Both.** Declared estate for the report, generated estates for the
robustness check. This is the right long-term answer and is listed in
`docs/known-limitations.md` as the main thing missing. It was cut because a
generator good enough to be evidence is a second project, and a generator not
good enough to be evidence is worse than none — it launders a single instance
into an apparent distribution.
