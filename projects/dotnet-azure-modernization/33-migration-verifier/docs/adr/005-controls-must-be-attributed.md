# ADR 005: A control counts only if the verifier catches that control's own damage

**Status:** accepted
**Date:** after every control passed and the gate turned out to be measuring nothing

## Context

`CutoverGate` exists to answer a question no dashboard answers: is this verifier's silence
evidence? It plants known defects -- five defective migrators -- and requires the verifier to be
observed catching them before a clean report is allowed to mean anything. Mutation testing,
applied to a migration.

The first implementation credited a control as caught if the verifier reported *any* difference
when run against that control's output:

```java
if (!comparison.differences(source, target).isEmpty()) caught++;   // wrong
```

Every control passed. Every verifier configuration scored 5 of 5, including ones the blindfold
matrix had already shown to be substantially blind. The gate said `GO` in situations where the
report three sections earlier said the verifier could not see.

The reason is specific to this corpus and general in its shape. Two rows -- the account number
that loses its leading zeros and the amount too large for a double -- are corrupted by the
*engine pair itself*, under any migrator, including the faithful one. So the verifier was never
silent. It always had those two differences to report, and the gate credited them to whichever
control happened to be running.

**A positive control that is positive whatever you do is not a control.** It measures the
apparatus' baseline, not the apparatus' sensitivity.

## Decision

A control is caught if and only if the verifier flags at least one row that *this control
damaged*:

```java
Set<Long> planted = rows this control damages, from Migrator.damages(row);
Set<Long> reported = ids the verifier flagged;
if (!Collections.disjoint(reported, planted)) caught++;
```

And a control that damages nothing in this corpus is excluded from the denominator entirely,
rather than counted as missed -- otherwise the corpus's coverage gaps get reported as the
verifier's blindness. The gate reports `caught / applicable`.

## Consequences

The numbers changed and the conclusion inverted, which is the point.

| verifier | before | after |
|---|---|---|
| full rule set | 5/5, `GO`-adjacent | **3/5, `NO_GO_BLIND`** |
| row count | 5/5 | **0/5, `NO_GO_BLIND`** |
| injective rules only | 5/5 | 5/5, `NO_GO_CORRUPTION` |

The row-count verifier going from 5/5 to 0/5 is the clearest indictment. It compares two
integers. It cannot detect a value-level defect by construction. Under the old rule it scored
full marks.

The surviving result is stronger than the one it replaced: **the configuration with perfect
precision on the faithful migration -- zero false positives, the one anybody would ship -- fails
its own controls.** Only the injective-rule set passes them, and that set is derived
independently in section 11 from ADR 001. Two lines of reasoning, one from mutation testing and
one from a definition, arriving at the same rule set.

It also produced the distinction the gate is actually for. `NO_GO_CORRUPTION` and `NO_GO_BLIND`
are both refusals, and they mean opposite things. One says the data is bad. The other says you
do not know whether the data is bad. A conventional check cannot express the second, and the
second is the one that ruins cutovers.

## Note on the ordering of checks

The gate tests blindness before it reports corruption. A verifier that fails its controls is not
permitted to be believed when it *does* find something, because the same rules that hid two
planted defects may be hiding others alongside the ones it found. `NO_GO_BLIND` therefore
outranks `NO_GO_CORRUPTION` -- both stop the cutover, but they send you to different places.
