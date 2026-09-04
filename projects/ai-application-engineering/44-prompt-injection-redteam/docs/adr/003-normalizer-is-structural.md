# ADR-0003: Classify the normaliser as a structural layer

**Status:** accepted

## Context

The report's organising distinction is structural versus behavioural. A
structural layer's guarantee is independent of what the model does; a
behavioural layer changes the probability that the model does something.

The broker and the egress filter are clearly structural: they act on a
concrete proposed action after the model has produced it. The classifier and
spotlighting are clearly behavioural: one rejects inputs on a score, the other
changes what the model sees.

The normaliser is the awkward one. It runs *before* the model, like the
behavioural layers, and it changes no outcome by itself.

## Decision

`Layer.NORMALIZE.structural` is `True`.

## Rationale

The criterion is not *where* a layer sits, it is whether its guarantee is
conditional on model behaviour. Normalisation is a deterministic function of
its input. Given the same bytes it produces the same bytes, forever, for every
model. "Unicode tag-block characters do not reach the model" is a statement
that holds under a model upgrade, a temperature change, and a different vendor.

Compare the classifier, whose statement is "payloads scoring above 1.0 are
rejected" — true, but the thing you want to know is whether *attacks* are
rejected, and that depends on a weight table that was chosen rather than
fitted (ADR-0005).

## Consequences

**Its Shapley value is small and its interaction is large.** 1.7% solo against
4.7% with the classifier. Normalisation is a *precondition* for a behavioural
layer rather than a defence in its own right, and the report says so.

**This classification is what exposed bug 2.** Because the taxonomy predicted
"near zero solo, large interaction", an *exact* zero in both was visibly
wrong — a layer with genuinely no effect scores near zero, with floating-point
dust from the factorial weights, not `0.0` twice. The layer was disconnected
from the classifier entirely. The taxonomy generated a prediction precise
enough to be falsified by a rounding artefact.

**A reader may reasonably disagree**, and the disagreement is cheap to act on:
`structural` is one property on one enum, and the report derives its
section-3 groupings from it. Flipping it changes which lines section 3 sweeps,
not any measured number.
