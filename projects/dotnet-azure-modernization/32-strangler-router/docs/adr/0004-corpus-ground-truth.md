# ADR 0004 — A mislabelled corpus looks exactly like a weak differ

**Status:** accepted

## Context

The central claim of this project is a measurement: ruleset X catches N of M
defects. That measurement is only worth anything if the M labels are right.

The first version of the corpus generator had a bug that makes the point better
than any argument could. The `customer_tier_changed` defect was implemented as:

```go
case CustomerTierChanged:
    meta["customerTier"] = "standard"
```

The base document picks a tier uniformly from `{gold, silver, standard}`. So
roughly one time in three, the "defect" assigned the value that was already
there and **changed nothing at all**. The pair was labelled defective. Its two
sides were byte-identical.

Every ruleset was then charged with a false negative it could not possibly have
avoided. The scoreboard read:

```
precise    TP 790  FP 0  FN 22   recall 0.973
```

`precise` looked like it had a 2.7% blind spot. It did not. It had a perfect
score and a broken ruler. The number was low enough to look plausible — which is
the dangerous kind of wrong — and it would have been quoted in the README as a
finding.

## Decision

Two rules for the corpus, both enforced by tests rather than by care.

**1. Every labelled defect must always mutate the document.** The tier defect
now downgrades deterministically (`gold → silver → standard → gold`) so it can
never be a no-op. `TestEveryDefectAlwaysMutates` applies each defect against 300
different base documents and fails if any application leaves the marshalled
bytes unchanged. The many-seeds loop is deliberate: the original bug was
data-dependent and would have survived a single-seed test.

**2. Noise must never touch a field a human would call a bug.**
`TestNoiseLeavesBusinessFieldsAlone` asserts that across 300 clean pairs, the
order id, status, currency, total, tax and line count are identical on both
sides. Without it, the false-positive column would be measuring the generator
rather than the rulesets — and the whole argument rests on that column reading
zero.

Two further properties are pinned because the tables in the README depend on
them: generation is a pure function of its seed, and defects are assigned
round-robin so no defect is under-sampled in the per-defect breakdown.

## Consequences

- `precise` scores 812/812 with 0 false positives. That is a real result, and it
  is a stronger result than the one the bug was hiding.
- The corpus has more tests than some of the production code, which is correct:
  it is the instrument, and an instrument you have not calibrated is decoration.
- The general lesson generalises past this project. When a measurement of a
  detector comes back slightly worse than expected, the first hypothesis should
  be that the ground truth is wrong, not that the detector is. Slightly-wrong
  numbers are more dangerous than obviously-wrong ones because they get
  published.
