# ADR 001: The embedding is lexical, and that does not change any conclusion

**Status:** accepted
**Date:** during design, revisited after §4 and §8 were measured

## Context

The project compares vector retrieval with graph traversal. The obvious objection to
any such comparison is that the vector side was crippled -- that a real sentence
encoder would have closed the gap and the whole report is an argument against a
strawman.

This environment has no model API and no network access at runtime. The available
choices were: a character 4-gram TF-IDF model, or nothing.

## Decision

Use character 4-gram TF-IDF with L2-normalised sparse vectors and cosine similarity,
and then **measure the objection rather than argue about it**.

Two independent measurements address it:

- **§4 (oracle retriever).** Hand the vector system exactly the premise documents and
  nothing else. It scores 16/16 -- identical to the graph. The reasoning machinery is
  shared and it works. Every multi-hop failure is therefore a *retrieval* failure, and
  the question becomes narrower: would a better encoder retrieve the conjunction?
- **§8 (worst-premise rank).** For each question, compute the k a perfect-recall
  retriever would need. Mean values: literal lookups 2.0, paraphrased 4.5, multi-hop
  42.3. The paraphrase penalty is real and is exactly what a neural encoder removes.
  It is an order of magnitude smaller than the multi-hop penalty.

## Why the multi-hop penalty is not an encoder problem

The document that must be retrieved in the middle of a chain is "Baltic Freight AG is
controlled by Silverline Holdings SA". The question is "which suppliers of Ashford
Components are ultimately controlled by a sanctioned person". These have no lexical
relationship, and they have no semantic relationship either -- the sentence is not
*about* Ashford Components under any encoder, because it is not about Ashford
Components. It is relevant by composition.

A better encoder ranks each document better against the query. It does not change what
the query is about. Ranking optimises each item independently; a multi-hop answer needs
a conjunction. That is a property of the retrieval formulation, not of the embedding
model, and no amount of encoder quality alters it.

## Consequences

- The paraphrase numbers in this report are **pessimistic** for a neural system, and
  §8 says so explicitly and quantifies by how much.
- The multi-hop numbers are **not** pessimistic in any way that matters, and §4 is the
  evidence.
- Anyone who disbelieves this can substitute an encoder behind the `Embedding`
  interface and re-run; the report regenerates from code. The prediction registered in
  §8 is the one to check.

## What was rejected

**Hand-tuning the embedding to look worse or better.** The temptation in a comparison
project is to tune the baseline until the story is clean. The tie-break in
`Retriever.topK` documents the opposite instinct: it is dead code that survives mutation
testing, and rather than delete it or fake a test for it, the reason it is unobservable
is written down.
