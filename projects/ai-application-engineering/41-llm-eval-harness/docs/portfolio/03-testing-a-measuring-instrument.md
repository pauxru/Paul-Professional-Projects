# Building a measuring instrument that can be checked

This project's deliverable is a document. `docs/results.md` contains about
forty tables of numbers, each supporting a claim about how a common evaluation
practice fails. The entire value of it rests on those numbers being right.

There is no external oracle. No published dataset to compare against, no
reference implementation, no colleague who already knows the answer. The
report is the only artefact, produced by code I wrote, describing a simulation
I designed, and there is nothing to check it against except itself.

That is a specific engineering problem and most of the work went into it.

## Predictions before measurements, enforced by the type system

The report is written through a small DSL:

```python
rep.expect("Power at n=50 will be under 25% for a +0.05 effect.")
power = power_paired(50, 0.05, sd)
rep.found(f"Power at n=50 is {pct(power)}.", contradicted=power >= 0.25)
```

`Report` holds at most one open prediction. Calling `found()` with none open
raises. Calling `expect()` twice raises. `render()` refuses to produce a
document while a prediction is unresolved.

The point is not tidiness. It makes one specific dishonest act mechanically
impossible: **you cannot quietly drop a prediction that turned out wrong.**
Once written, it appears in the output or the build fails.

That prediction above is real and it was wrong. Measured power at n = 50 is
50.9%, and the report says so, marked. Three of seventeen predictions were
contradicted. A test asserts that the count never reaches zero:

```python
def test_some_predictions_were_wrong(self, committed):
    """A report where every prediction held is a report where the predictions
    were written after the measurements."""
    assert committed.count("prediction wrong") >= 2
```

Each contradiction was also worth more than the prediction would have been.
The power result forced the question of *why* 50.9%, which led to the paired
standard deviation being far below the independent-systems value, which led to
the finding that eval set adequacy is a property of how similar the two
systems are -- the most useful thing in the report. It does not exist if the
prediction was right.

## The report is a build artefact and is hashed like one

```python
EXPECTED_SHA = "5cbb6688589b598a"

def test_regenerating_produces_identical_bytes(self, committed, regenerated):
    assert sha(regenerated) == sha(committed)
```

The test regenerates the entire report into a temporary directory and compares
bytes. Every random draw is seeded. Text wrapping is hand-rolled rather than
`textwrap`, specifically so that a standard library change cannot break the
comparison.

If a change to the library moves a number, this fails, and the fix is to
regenerate and update the hash **in the same commit**. Never on its own. A
committed report whose numbers no longer follow from the committed code is
worse than no report, because it looks like evidence.

## Structural checks on the prose

Reproducibility only proves the document is a deterministic function of the
code. It says nothing about whether the document is any good. So there are
assertions on the artefact itself:

- Every prediction has exactly one finding. Counted on `**Found` and not
  `**Found.**`, because a contradicted finding renders as `**Found --
  prediction wrong.**`, and matching the clean form only would silently permit
  dropping every failed prediction -- defeating the check above.
- No `TODO`, `PLACEHOLDER`, `nan`, `inf`.
- No leaked Python reprs: `np.float64`, `array([`, `<Verdict.`, `dtype=`.
  These are what a formatting bug actually looks like.
- Every markdown table has a consistent column count, a separator row, and at
  least one data row.
- Percentages are bounded above by 1000, not 100 -- section 6 legitimately
  reports 107.1% of an apparent gain evaporating -- but a divide-by-tiny is
  caught.
- The report discloses within its first 6,000 characters that no LLM was
  called, and links `known-limitations.md`.

That last one is a test because it is the claim most likely to be quietly
dropped in a rewrite, and it is the one that makes the difference between an
honest document and a misleading one.

## Testing a simulation

The hardest part. A simulation cannot be tested against reality -- that is why
it exists. It can be tested against the properties it is *supposed to have*,
and that turns out to be enough, but only if the properties are chosen well.

The failure that matters is not a crash. It is a simulation that produces a
plausible report arguing for the wrong thing. Two of the six catalogued bugs
were exactly that, and neither was found by testing implementation details:

```python
def test_judge_noise_does_not_cancel_completely_in_paired_differences():
    sds = [paired_sd(noise=nz) for nz in (0.001, 0.05, 0.20)]
    assert sds[0] < sds[1] < sds[2]
```

This asserts a *statistical relationship between two runs*. It reads no seeds
and no keys. The bug it caught -- judge noise cancelling exactly in paired
differences, making the report conclude judge quality is irrelevant to A/B
decisions -- passes every test that examines a single run. "The judge is
deterministic for a given item and response": passes. "Noise makes scores
vary": passes. The defect lived entirely in the relationship, and only a test
of the relationship could see it.

The same shape appears throughout:

- `shared_variance` must reduce paired spread monotonically.
- Growing the dataset must not change any existing item's score.
- `variant(quality_delta=d)` must recover `d` at large n.
- Two judges with equal marginal agreement may require different eval set
  sizes -- which is only true if the error decomposition is right.

## Testing the statistics without scipy

No scipy in this environment, so the bootstrap, BCa, permutation test,
Benjamini-Hochberg, normal and Student-t quantiles, and the exact sign test are
implemented from primitives. That began as a constraint and became the most
useful part of the exercise, because it forced every one to be validated
directly:

- `student_t_ppf` against published t-tables to 1e-4.
- `sign_test(5, 20)` against the tabulated 0.04139, and `sign_test(1, 5)`
  against a hand-computed `2 * 6/32`.
- The incomplete beta against its own symmetry identity.
- Interval coverage measured by simulation: 91.0% / 92.2% / 95.8% at n = 20 /
  60 / 200 for a nominal 95%.

That last one is the test I would keep if I could keep only one. It does not
check that the code matches a formula; it checks that the interval does the
job the interval claims to do. It is also how the BCa degeneracy bug surfaced.

## Calibrating the gate

`compare()` returns a verdict, and a verdict that is not calibrated is
decoration. Section 12 runs 200 trials per scenario at n = 120 with a known
effect:

| true effect | REGRESSED | IMPROVED | UNDERPOWERED |
|---|---|---|---|
| -0.06 | 97% | **0%** | -- |
| 0.00 | -- | 1.5% | 92% |
| +0.06 | -- | 92% | -- |

The 0% is the number that matters: a genuinely harmful change is never called
an improvement. A gate can be wrong in several ways and they are not equally
bad.

## What this adds up to

Six independent checks, none sufficient alone:

1. Predictions written before measurements, enforced mechanically.
2. Byte-level reproducibility, hash-pinned.
3. Structural assertions on the generated document.
4. Statistical-property tests on the simulation.
5. Direct validation of every statistical primitive against known values.
6. End-to-end calibration of the verdicts against known effects.

Layer 4 caught bugs 1 and 6. Layer 5 caught bug 3. Layer 3 caught two
formatting errors. Layer 1 produced the report's best finding by being wrong.

The general claim: when the deliverable is a set of numbers and there is no
external oracle, the tests cannot target the code. They have to target the
*claims* -- and writing them that way is what turns a document that asserts
things into one that can be checked.
