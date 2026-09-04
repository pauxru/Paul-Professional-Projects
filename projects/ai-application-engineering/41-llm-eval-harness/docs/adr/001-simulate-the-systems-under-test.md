# ADR 001: Simulate the systems under test

**Status:** Accepted

## Context

This is a harness for evaluating LLM systems. The obvious design calls a real
model, scores real outputs with a real judge, and reports the comparison.

Every claim this project wants to make has the shape *"the standard method
reports X when the truth is Y."* Section 9 asserts that a length-biased judge
reports +0.0711, with a significant interval, for a true effect of +0.0021.
Section 6 asserts that 107.1% of a sweep winner's apparent gain is selection
bias. Section 11 asserts that 8.5% of individual 50-item runs measure a real
improvement as a decline.

None of those sentences can be written about a real model. With a live API
there is no Y. You obtain a table of measurements and no way to say which of
them were wrong -- which is exactly the epistemic position this repository
argues is untenable, so building the argument on it would be self-refuting.

There is a second constraint. "How often does this go wrong?" is a frequency
question, and answering it means running the comparison thousands of times
under a known effect. Section 11 alone is 10,000 evaluations. At real API
prices and latencies those experiments do not get run, which is a substantial
part of why these numbers are not already widely known.

## Decision

Simulate the models and the judge. Do not simulate the statistics.

`SimulatedModel` and `SimulatedJudge` produce scores from a specified
generative process with parameters that are the ground truth. Everything
downstream -- the bootstrap, the BCa correction, the permutation test, the
Benjamini-Hochberg procedure, the Student-t quantiles, the exact sign test --
is a real implementation, written from primitives, validated against published
tables, and covered by tests.

The boundary is drawn so that the code a user would actually depend on is
real, and only the thing being measured is synthetic.

## Consequences

**What this buys.** Ground truth, so "wrong" is a decidable property.
Repetition, so "how often" is answerable. Determinism, so `docs/results.md` is
byte-reproducible and a test asserts it. Free parameter sweeps, which is how
section 1 shows that power moves from 79.0% to 28.5% purely by changing how
much two systems have in common -- an experiment that would otherwise require
manufacturing model pairs with prescribed correlations.

**What it costs.** Nothing here establishes anything about any specific real
model. The absolute numbers are properties of the simulation's parameters.
`docs/known-limitations.md` says so bluntly, and the report's own header does
too, because a document about LLM evaluation that does not prominently state
that no LLM was called is dishonest regardless of its statistics. A test
asserts that disclosure is present in the first 6,000 characters.

**Where the risk actually is.** A simulation that does not resemble reality in
the dimension being studied produces a plausible report that argues for the
wrong thing, which is worse than an obviously broken one. The dimension that
matters here is the correlation between two systems' item-level performance,
and getting it wrong is not a hypothetical -- see ADR 002. Two of the six
bugs catalogued in `docs/portfolio/04-bugs-the-experiment-found.md` were
modelling errors that made the simulation quietly argue the opposite of the
truth, and both were found by tests written against the *statistical* property
the simulation was supposed to have, not by reading the code.

## Alternatives considered

**Record real outputs once, replay forever.** Keeps ground truth out of reach;
a fixed corpus of a few thousand responses cannot support 10,000 independent
trials, and the effect size still has to be assumed rather than set.

**A small live study.** Would produce genuine numbers about one model at one
moment, with no power to make any of the frequency claims, and would go stale.

**A hybrid: simulate, then validate one section against a live model.** The
most attractive option and rejected only because no API access is available in
this environment. It would strengthen ADR 001 rather than replace it -- the
frequency claims would still need the simulation.
