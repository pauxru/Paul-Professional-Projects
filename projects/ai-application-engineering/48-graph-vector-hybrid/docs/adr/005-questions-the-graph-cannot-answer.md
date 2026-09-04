# ADR 005: The corpus contains questions the graph cannot answer

**Status:** accepted
**Date:** during design

## Context

A benchmark built by the author of one of the systems under test will flatter that
system. The usual mechanism is not fraud; it is that the questions get written while
thinking about what the graph can do, so every question happens to be a traversal.

## Decision

Deliberately seed the corpus with question classes the graph must lose:

- **`OPEN`** -- facts outside the extractor's five-relation schema. *"Who maintains the
  OFAC SDN List?"* is answered by one sentence in the corpus and by **no edge in the
  graph**, because MAINTAINS was never a relation the extractor knew about.
- **`PARAPHRASE`** -- questions worded unlike the stored relations, to test the usual
  argument for embeddings.
- **`NEGATIVE`** -- questions whose correct answer is the empty set. In compliance this
  is the majority case and the only one with a deadline: the operational question is not
  "who is sanctioned" but "may this shipment proceed".
- **`AGGREGATE`** -- counting questions, where the answer is a number that appears in no
  document.

`CorpusTest.openQuestionsAreOutsideTheSchema` asserts each OPEN question's premise
document states *no* relation, so the class cannot silently degrade into questions the
graph can answer after all. `RetrieverTest.multiHopQuestionsAreHard` asserts no
multi-hop question has full premise recall at k=5, so the multi-hop class cannot
silently degrade into easy ones.

## Consequences

The measured result contradicted the prediction, which is why the class was worth
having. **Paraphrase is not where the graph loses** -- it scores 2/2, because once a
question has become a plan its wording is irrelevant. The graph loses on **schema
coverage**: 3 of 3 open questions have no representation in its schema at all.

That is a structurally different and more serious limitation than the one the literature
usually cites. A graph's coverage is bounded by a schema fixed *before the questions were
known*. Retrieval has no schema and therefore no coverage limit -- it returns something
for any question, which is simultaneously its advantage in §6 and its failure mode in §7.

The honest summary is that the two substrates fail in ways that do not overlap, which is
the strongest available argument for the hybrid the report ends up recommending.

## What was rejected

**Scoring OPEN questions numerically against both systems.** They have no set-valued
ground truth -- the answer is a sentence. Scoring them would have required an answer
model, and the report would then be measuring that. §6 reports whether the premise
document was retrieved and whether the relation exists in the schema, which is the
checkable part of the question, and leaves the rest unclaimed.
