# ADR-0005: The classifier's weights are chosen, not fitted

**Status:** accepted

## Context

`InjectionClassifier` scores text by summing weights over fired signals:
`override_imperative` 0.8, `fence_forgery` 1.2, `invisible_payload` 1.5, and so
on. Those numbers were written by hand.

The obvious improvement is to fit them — logistic regression on the corpus
would take an afternoon and would certainly produce a better ROC curve.

## Decision

The weights stay hand-chosen, and the report labels every conclusion that
depends on them.

## Rationale

**Fitting on this corpus would measure the corpus.** 81 attacks in 8 families,
with 28 of them generated combinatorially from 4 base payloads. A classifier
fitted on that learns the shape of `corpus.py`. Its detection rate would be a
statement about a file in this repository, reported as though it were a
statement about prompt injection.

**A held-out split would not fix it.** The obfuscated family is 7 obfuscations
crossed with 4 payloads; any split leaks. Fixing that means a corpus large and
diverse enough to split honestly, which is a different project — and it would
have to be *real* attack data, which is the point at which this stops being a
harness and starts being a dataset.

**The structural results do not depend on the weights at all.** The broker's
flat line in section 3, the egress filter's channel analysis in section 7, the
delimiter result in section 8 — none of these involve the classifier. That is
the payoff of the structural/behavioural split: an unfitted component
contaminates a bounded, identified part of the report.

## Consequences

**Section 6's contradiction is a consequence of this decision.** The
prediction was that reaching 90% detection would cost an unacceptable false
positive rate. The finding is that **no threshold in the swept range reaches
90% detection at all** — the tradeoff is not adverse, it is unavailable. With
fitted weights the curve would look better and the section would say something
weaker and less true.

**Section 4's contradiction carries a Grade B caveat for the same reason.**
Spotlighting outranks the classifier on Shapley value, and the report states
plainly that this rank reflects `spotlight_effect` — a chosen parameter of the
simulated target — rather than a measured property of either defence.

**Grade labels appear throughout the report.** Grade A: holds by construction,
independent of the model and of these weights. Grade B: depends on a stated
parameter that was chosen rather than measured. A reader who disagrees with a
parameter can find every claim that rests on it.
