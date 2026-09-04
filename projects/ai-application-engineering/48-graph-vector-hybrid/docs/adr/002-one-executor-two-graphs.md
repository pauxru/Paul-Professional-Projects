# ADR 002: One executor, two graphs -- the systems differ in substrate only

**Status:** accepted
**Date:** during design

## Context

A comparison between "RAG" and "a knowledge graph" normally compares two entire
pipelines: an embedding model, a chunker, a retriever and an LLM reader on one side; an
extractor, a resolver, a store and a query language on the other. Every measured
difference is then attributable to any of eight components, and the conclusion is
whatever the author wanted it to be.

## Decision

Build **one** query executor. Hand it **two** different graphs.

- The graph system receives the full extracted graph.
- The vector system receives the subgraph induced by its top-k retrieved documents.

Everything downstream -- traversal, guards, composition, deduplication, provenance,
scoring -- is literally the same code path.

## Consequences

### The retriever's reader becomes perfect

Since the vector system's "reader" is the executor, it never hallucinates, never
misreads a passage, and never fails to compose two facts that were both in its window.
Real LLM readers do all three.

This is the point. It means:

- Every number reported for the vector system is an **upper bound** on what any RAG
  pipeline over this corpus could achieve.
- Every failure measured is a **floor**, not an artefact of a weak baseline.
- A result that survives being this generous to the thing being criticised is worth
  considerably more than one that does not.

### The comparison becomes about one variable

"Given a correct plan and a perfect reader, can this substrate support this question?"
is a question with a clean answer. "Is RAG worse than graphs?" is not.

### It costs realism, and §7 is where that shows

A generative reader over the same passages would produce false alarms the executor
cannot produce -- it sees three entities and the word *sanctioned* in one window, and
the passages that would rule out a connection are not there to rule it out. §7 states
this explicitly rather than claiming the vector system's clean negative-question
performance transfers to a real RAG stack. **Absence is not retrievable**, and the
executor's silence on negatives is a property of the experimental design, not evidence
about RAG.

## What was rejected

**Giving each system its own natural query interface.** More realistic, and
uninterpretable: a difference could come from the substrate or from the interface, and
there would be no way to separate them.

**Adding an LLM reader to the vector side.** Not possible in this environment, and it
would have made the vector numbers *worse* while making them less conclusive -- any
failure could be blamed on the reader. The current design forecloses that objection
entirely.
