# ADR 002: Decompose model quality into shared and private variance

**Status:** Accepted
**Amends:** ADR 001

## Context

The first version of `SimulatedModel` drew each model's quality on each item
from its own independent stream. Two models being compared were statistically
independent, item by item.

That is wrong, and wrong in a way that inverts the report's conclusions.

Two prompts against the same base model do not fail independently. A question
that is ambiguous is ambiguous for both. A reference answer that is subtly
incorrect penalises both. The item-level performance of two systems in an A/B
test is strongly correlated, and that correlation is the entire reason paired
analysis works: pairing cancels the shared component and leaves only the part
that actually differs.

With independent models the paired difference has variance
`sd_a^2 + sd_b^2` -- *larger* than either system alone. Paired analysis then
buys nothing, the power calculations describe a world where 50-item eval sets
are hopeless for any effect, and the report would have concluded that pairing
is ceremony. With perfectly correlated models the paired variance goes to zero
and tiny eval sets look sufficient. Both are artefacts of the modelling
choice, and neither is visible in the output: both produce clean tables.

## Decision

Split quality on an item into two components:

```
quality = base + tier_offset[tier] + spread * (sqrt(rho) * shared + sqrt(1-rho) * private)
```

`shared` is keyed on the item id alone, so every model in a comparison draws
the identical value. `private` is keyed on the item id and the model identity.
`rho` -- `shared_variance` -- is the fraction of variance that is a property of
the item rather than of the model on that item. It is the intraclass
correlation, and it is exposed as a parameter rather than fixed.

## Consequences

Measured paired standard deviation moves from 0.238 at rho = 0 to 0.107 at
rho = 0.9. Section 1 uses this directly: holding the eval set, the effect and
the judge fixed and varying only rho moves the power of a 50-item eval set
from 28.5% to 79.0%.

That result reframes the report's headline question. "Is 50 items enough?" has
no answer. **Eval set adequacy is a property of how similar the two systems
being compared are**, and a team that can answer "is 50 enough" for a prompt
tweak has not thereby answered it for a model swap. This is the most useful
thing in the report and it exists only because the parameter is explicit.

A secondary finding fell out: even at rho = 0 the measured score correlation
between two models is 0.449, not 0. The tier structure is shared -- both
models find adversarial items harder -- so the composition of the eval set
supplies some pairing benefit for free, before any modelling of item
difficulty. That is true of real eval sets too and is not usually noticed.

The same reasoning was later applied to the judge, which had the identical
defect in a more damaging form (ADR 003).

## Alternatives considered

**Fix rho at a plausible constant.** Simpler, and it would have produced a
correct report with a weaker argument -- section 1's best finding would not
exist, and the reader would have to take the constant on faith.

**Draw a per-item difficulty and add it to both models.** Equivalent for
rho = 0.5 and awkward to vary continuously. The variance-fraction
parameterisation makes the intraclass correlation directly readable, which
matters because it is the quantity a reader would need to estimate for their
own eval set.

**Model the correlation at the score level instead, via a copula or a
correlation matrix.** More general, and it separates the correlation from any
account of *why* it exists. The item-effect formulation says something a
practitioner can act on: the shared component is item difficulty, so measuring
it means scoring two systems on the same items and looking at the correlation
-- which the report then does.
