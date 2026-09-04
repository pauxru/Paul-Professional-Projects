# Two mutants survived, and both tests were comparing the code to itself

I run mutation testing on these projects because a passing test suite is evidence that the
code does *something*, not that it does the right thing. `tools/mutate.py` makes twelve
deliberate, semantically meaningful edits — remove the persistence rule, make the CUSUM
two-sided, let the bootstrap ignore estimation error — and asserts the suite fails on each.

Ten died immediately. Two survived. Neither survivor was a missing test. Both were tests
that could not fail, for the same underlying reason in two different costumes.

---

## Survivor 1: a tautology dressed as an assertion

The `material-day-without-persistence` mutant removes the 3-day persistence requirement from
`first_materially_degraded_day` — the ground truth every detector is scored against. The
test guarding it read, in effect:

```python
loose = first_materially_degraded_day(quality, persistence=1)
strict = first_materially_degraded_day(quality, persistence=3)
assert loose <= strict
```

Which is true. It is also true of any implementation, correct or not, because relaxing a
conjunction can only move the first satisfying day earlier or leave it. It is a theorem
about the shape of the predicate, not a claim about this function. It would pass on a
function that ignored `persistence` entirely and returned the same day for both — which is
exactly what the mutant does.

The fix was to pin the numbers: 52 under `persistence=1`, 57 under `persistence=3`. Mutant
killed.

Pinning those two values also documents something the inequality hid. A five-day gap between
the loose and strict answer means `template-regression`'s quality curve sits almost exactly
on the materiality line, and its "first material day" was being decided by which side of
0.95 the daily noise landed on. A five-day swing in a *ground truth*, driven by noise,
against which every detector's delay was being measured. I fixed that too — truncated
answers went from quality 0.4 to 0.1 — but I only saw it because the tautology forced me to
write down what the function actually returns.

## Survivor 2: an oracle that was the implementation

The `psi-reference-positional-misalignment` mutant scrambles the column ordering inside
`PSIReference`, the cached-quantile class I introduced when I optimised the run from 319
seconds to 32. The test guarding it compared `PSIReference`'s output against
`population_stability_index`.

The problem: as part of that same optimisation, `population_stability_index` had been
rewritten to *call* `PSIReference` internally. The test was comparing the code to itself.
It would agree with any mutation, correct or not, because both sides mutate together.

This is more insidious than the tautology, because the test was *correct when written*. It
became vacuous through a refactor that nothing flagged — the assertion still ran, still
compared two things, and still passed. Only mutation testing noticed that it had stopped
being able to fail.

The fix was to write an independent oracle: `_reference_psi`, a naive, slow,
obviously-correct PSI implemented directly in the test file with explicit loops and
`np.histogram`. It is the implementation I would have written if I did not care about speed,
which is the right shape for an oracle. Mutant killed.

## The bug the second fix uncovered

Making the two implementations agree exposed a real defect that had been sitting in the
degenerate-bin guard since the first version:

```python
if len(np.unique(edges)) < 3:   # unreachable
```

The bin edges are built with `-inf` and `+inf` as the outer edges, so `np.unique` always
returns at least those two distinct values plus whatever is between them. For any finite
input this condition is never true. The guard I had written to handle constant columns had
never once executed.

That is not merely dead code. A constant reference dimension produces bin edges that map the
entire real line into a single bin, so a genuine shift in that dimension scores exactly zero
drift — the detector reports "nothing happened" with maximum confidence about the one
dimension where something might have. The guard was supposed to catch that and did not.

The replacement checks what I actually meant: `np.ptp(column) == 0.0` or a non-finite value
present. Results across the whole panel were byte-identical afterwards, because no dimension
in this corpus is constant — so it is a pure robustness gain with zero effect on any number
in `results.md`, and I know that because the determinism stage compares report hashes rather
than because I reasoned about it.

---

## What this says about mutation testing

The usual pitch is that it finds *missing* tests. In my experience it more often finds
**tests that exist, pass, and cannot fail** — and that is a strictly harder problem, because
a missing test is visible in a coverage report and a vacuous test is not. Both of mine had
100% line coverage over the code they were supposedly guarding. Both would have survived any
review that asked "is this tested?".

The pattern shared by both survivors is worth naming, because I now look for it directly:
**an assertion whose two sides cannot disagree.** In the first case one side was a logical
consequence of the other. In the second, one side literally called the other. A test is only
as strong as the independence between what it computes and what it checks, and refactoring
erodes that independence silently, because nothing in the toolchain knows that a test's
oracle was supposed to be independent.

The cheapest defence I have found is the one used here: when a test's expected value is
computed rather than written down, write the oracle in the test file, slowly and stupidly,
and never let it import from the module under test.
