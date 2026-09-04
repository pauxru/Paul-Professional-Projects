# The bug in the ruler

The headline result of this project is that `precise` scores 812 out of 812 with
zero false positives.

The first time it ran, it scored 790.

```
ruleset               TP      FP      FN     precision    recall
------------------------------------------------------------------
precise              790       0      22         1.000     0.973
```

Recall 0.973. A 2.7% blind spot. That is a *good-looking* number — good enough
to publish, small enough to feel honest, and it would have gone straight into
the README as a finding: "even the tightest ruleset has a residual miss rate of
around 3%."

The per-defect breakdown said where:

```
customer_tier_changed        MISSED 16/40
```

Forty per cent of one defect. Not a pattern that looks like a differ weakness —
a differ that can compare `"gold"` against `"silver"` can do it every time or
never. Forty per cent is the fingerprint of something data-dependent.

## What it actually was

```go
case CustomerTierChanged:
    meta["customerTier"] = "standard"
```

The base document picks a tier uniformly from `{gold, silver, standard}`. One
time in three, the "defect" assigned the value that was already there.

The pair was labelled defective. Its two sides were **byte-identical**. Every
ruleset was being charged with a false negative that no detector could avoid.
`precise` did not have a 2.7% blind spot. It had a perfect score and a broken
ruler.

## Why this is the most useful thing in the repository

The failure is not that a generator had a bug. The failure is that the bug
produced a number that looked right.

An obviously-wrong measurement gets caught. 0.973 is not obviously wrong. It is
plausible, it is modest, it flatters the author by admitting a limitation, and
it is completely fictional. Slightly-wrong numbers are more dangerous than
badly-wrong ones precisely because they survive review.

The general form, which applies well beyond this project: **when a measurement
of a detector comes back slightly worse than expected, the first hypothesis
should be that the ground truth is wrong, not that the detector is.** Everyone's
instinct runs the other way, because the detector is the thing you built and the
corpus is "just test data".

The corpus is not test data. It is the instrument. An instrument you have not
calibrated is decoration.

## What was done about it

The fix was three lines — downgrade deterministically, never a no-op. The
interesting part is the tests that now hold it in place:

```go
func TestEveryDefectAlwaysMutates(t *testing.T) {
    for _, d := range AllDefects {
        for seed := uint64(0); seed < 300; seed++ {
            doc := base(NewRng(seed))
            before, _ := json.Marshal(doc)
            applyDefect(doc, d, NewRng(seed))
            after, _ := json.Marshal(doc)
            // a labelled defect that is sometimes a no-op is a mislabelled corpus
        }
    }
}
```

Three hundred seeds, not one. The original bug was data-dependent; a
single-seed test would have passed two times in three and been *worse* than no
test, because it would have looked like coverage.

Its sibling asserts the other direction — that noise never touches a field a
human would call a bug:

```go
func TestNoiseLeavesBusinessFieldsAlone(t *testing.T)
    // order id, status, currency, total, tax, line count identical across 300 clean pairs
```

Without that one, the false-positive column would be measuring the generator
rather than the rulesets. And the entire argument of this project rests on that
column reading zero.

The corpus package now has more tests than some of the production packages. That
is the correct ratio.

## The result after the fix

```
precise              812       0       0         1.000     1.000
```

The real number was better than the fictional one. That is not always how it
goes — but it is a reminder that "we found a bug in our measurement" is not a
setback. It is the measurement starting to work.
