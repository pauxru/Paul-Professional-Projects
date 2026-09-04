# ADR 0005: measure the distribution distortion instead of assuming it away

**Status:** accepted

## Context

Constrained decoding is universally described as "the model can only produce
valid output". That is true. It is also universally *implied* that the result is
the model's distribution restricted to valid outputs. That is false, and the
gap is not small.

At each step the decoder takes the model's distribution over tokens, zeroes the
illegal ones, and renormalises over what is left. Call the result **masked
decoding**. What people believe they are getting is

> P(document | document is valid)

which requires dividing the model's probability of a document by the total
probability the model assigns to *all* valid documents. Masked decoding never
computes that total. It renormalises locally, over the tokens legal right now,
with no knowledge of how much valid probability mass lies behind each choice.

A token that is locally likely but leads to few or unlikely valid completions
gets systematically over-weighted.

## Decision

Build the machinery to compute the true conditional exactly, on cases small
enough to enumerate, and report the difference as a first-class result.

Concretely:
- a bigram model, so the partition function over valid completions is
  computable by dynamic programming;
- a product graph of (DFA state × previous token), enumerated exhaustively;
- both distributions computed in closed form;
- KL divergence and total variation between them, at the token-sequence level
  and marginalised to the document level;
- and the **corrected** decoder — reweighting each candidate by the total valid
  mass behind it — implemented independently and asserted to reproduce the exact
  conditional.

## Consequences

The distortion is large. Document-level KL up to **1.42 nats**, total variation
up to **0.55**, and in 4 of 15 measured configurations *the most likely valid
document is not the same document* under the two methods.

The corrected decoder reproduces the exact conditional to within 1e-12. This is
the useful negative result: the correction is *right*, and it is *unusable*,
because computing the valid mass behind a token requires summing over all valid
completions — trivial for a bigram, impossible for a transformer.

A second finding fell out of the same machinery. The map from token sequences to
documents is many-to-one: one document had **14 distinct tokenisations** in the
benchmark vocabulary. The probability of a *document* is therefore a sum over
its tokenisations, and no left-to-right decoder computes that sum either. So
even a hypothetical decoder with perfect lookahead would still be sampling from
the wrong space unless it marginalised over tokenisation.

The practical upshot, which is what the README leads with: constrained decoding
buys guaranteed validity at the cost of a measurable, invisible bias. Both halves
of that sentence should be stated to anyone deciding whether to use it.

## Alternatives considered

**Use a real language model and estimate the divergence by sampling.** Rejected.
The quantity of interest is a ratio of partition functions; estimating it from
samples of a distribution that already excludes invalid output is
circular — you cannot sample your way to the normalising constant you are
missing. Exact computation on a small model says something true; a sampled
estimate on a big model would have said something impressive and unfalsifiable.

**Report only the mechanics (throughput, state counts) and skip the statistics.**
Rejected: those are the easy numbers, and they are the ones every other
implementation already reports.
