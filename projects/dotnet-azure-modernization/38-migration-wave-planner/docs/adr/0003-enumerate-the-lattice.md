# 3. Enumerate the lattice, not the layers

**Status:** accepted — after the same bug was written twice

## Context

Two places in this model search a space of subsets for a cheapest feasible
one:

- `cheapest_feasible_decoupling` — which of the ten remediable edges should we
  pay to break, so that every migration unit fits in a wave?
- `_hybrid_build_bound` — which set of dependencies must be paid for as hybrid
  links under *any* valid plan?

Both were written the same way, months apart, by me. Enumerate subsets in
increasing order of cardinality; stop at the first size where a feasible
subset appears; return the cheapest subset of that size.

Both docstrings claimed to return the cheapest feasible answer.

## The bug

Feasibility is monotone in cardinality: if breaking `k` edges makes the estate
feasible, breaking `k+1` including those does too. So the *smallest feasible
cardinality* is well defined and the search terminates correctly.

Cost is not monotone in cardinality. Two cheap edges can cost less than one
expensive one. The smallest feasible set and the cheapest feasible set are
different objects, and nothing in either implementation connected them.

The failure is silent. The function returns a real, feasible, valid answer.
It is just not the cheapest one, and it is labelled as though it were. Nothing
downstream can detect this — every consumer of the result treats it as
optimal, and the number it produces is plausible.

## Decision

Enumerate the full lattice. Both functions now iterate all `2^n` subsets with
a bitmask, test feasibility, and keep the cheapest feasible one. `n` is 10 and
13 respectively; 1,024 and 8,192 evaluations are nothing next to the annealer's
11,318.

Both now report `evaluated`, `subsets` and `feasible` counts in their return
value, and the report prints them, so the claim "this is exact" is backed by a
visible count rather than by a docstring.

## Consequences

**On this estate, the decoupling answer did not change.** £183,000, six edges,
40 of 1,024 subsets feasible. The *claim* was wrong, not the number. That is
the uncomfortable part: the bug had been shipping a correct answer, and no
amount of eyeballing the output would have found it.

**The bound answer did change**, and it changed the headline gap, which is why
this is an ADR and not a comment.

**Tests now assert optimality directly.** `tests/test_units.py` brute-forces
the same question two independent ways and compares. `tests/test_bounds.py`
generates 200 random feasible plans and asserts the bound never exceeds any of
them, plus asserts it never exceeds the exact optimum on every sub-instance
the exact solver can handle. An optimality claim that is only checked by
reading the code is not checked.

**The general rule this is an instance of.** When a search stops early, the
stopping rule must be a property of the *objective*, not of the constraint. A
layered search over subsets is correct for "smallest feasible" and incorrect
for "cheapest feasible", and the two read identically at the call site. The
tell is a docstring using a superlative the loop does not establish.

I wrote this bug twice because the layered shape *feels* like a smart
optimisation — it looks like a branch-and-bound with an obvious bound. It is
not; there is no bound involved. It is a search over the wrong ordering that
terminates confidently.

## Alternatives considered

**Branch and bound over cardinality layers.** Correct, and genuinely faster
for large `n`. Rejected because `n` is 10 and 13 here, so the full enumeration
costs nothing measurable, and a correct simple thing beats a correct clever
thing when the clever version is the one I have already got wrong twice. If
`n` grows past ~25 this becomes the right answer and the tests are already in
place to check it against the enumeration.

**Sort subsets by cost and stop at the first feasible one.** Also correct, and
avoids evaluating all of them. Rejected because generating subsets in cost
order requires a priority queue over `2^n` items, which is more machinery than
just checking all of them, and the early-exit is worth nothing at this size.
