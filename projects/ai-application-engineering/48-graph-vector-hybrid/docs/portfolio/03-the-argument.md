# 03 — The argument

Nine predictions, registered in code before the measurements that settle them. Four
held; five were contradicted. This essay follows the ones that changed the conclusion.

## The cliff, not the slope (P2, contradicted)

The expectation was a gradual fall-off: worse at three hops than at two, but still
finding some answers, because a large enough k often happens to contain the whole chain.

| hops | questions | vector k=10 | graph |
|---|---|---|---|
| 1 | 7 | 7/7 | 7/7 |
| 2 | 3 | 1/3 | 3/3 |
| 3 | 2 | 0/2 | 2/2 |
| 4 | 2 | 1/2 | 2/2 |

There is no slope. Retrieval works, and then it stops working, and the transition
happens between one hop and two.

## Why more retrieval does not help (P3, contradicted)

The standard remedy is to raise k. The prediction was that premise recall would climb
steadily and multi-hop performance would come back. It does not.

One question sits at 1 of 2 premises at **k=40 of an 80-document corpus** — half the
index, a k no production system would run — and only completes at k=80, at which point
the retriever has stopped being a retriever.

The mechanism is worth stating precisely, because it explains a familiar production
experience. A single-hop question needs one document. A four-hop question needs nine,
*simultaneously*, in the same window. And the documents in the middle of a chain do not
resemble the question:

> "Baltic Freight AG is controlled by Silverline Holdings SA"

shares nothing with

> "which suppliers of Ashford Components are ultimately controlled by a sanctioned
> person"

It is relevant by **composition**, not by similarity, and similarity is the only thing
an index knows. Ranking optimises each document independently; the answer needs a
conjunction. Raising k improves the easy questions, does nothing for the hard ones, and
costs monotonically more — which is exactly what teams report.

## The result that makes the argument (P4, held)

If the failure is conjunction retrieval, then handing the vector system exactly the
premise documents should fix it *completely*. If anything else is wrong — reasoning,
composition, the plan — it should not.

With an oracle retriever the vector system scores **16/16, identical to the graph**.

That single number does most of the work in this report:

- The shared reasoning machinery is correct, so no multi-hop failure is a reasoning
  failure.
- The entire deficit is the probability of retrieving a conjunction.
- **No encoder improvement changes the shape of that problem.** A better encoder ranks
  each document better against the query. It does not make the middle of a chain
  resemble the query, because that sentence is not *about* the query's subject under any
  encoder.

§8 confirms this from the other direction: the mean k needed for full premise recall is
2.0 for literal lookups, 4.5 for paraphrased, and **42.3 for multi-hop**. The paraphrase
penalty is real and is exactly what a neural encoder removes. It is an order of magnitude
smaller than the penalty no encoder removes.

## Where the graph loses, and it is not what everyone says (P6, contradicted)

The prediction was paraphrase — the standard argument for embeddings. Wrong: the graph
scores 2/2 on paraphrased questions, because once a question has become a plan its
wording is irrelevant.

The graph loses on **schema coverage**. Its extractor knows five relations. *"Who
maintains the OFAC SDN List?"* is answered by one sentence in the corpus and by no edge
in the graph, because MAINTAINS was never a relation anyone thought to extract. All 3
open questions are outside the schema; the retriever finds all 3 in its top 5.

This is a structurally worse limitation than paraphrase and it does not appear in the
usual comparison. **A graph's coverage is bounded by a schema fixed before the questions
were known.** Retrieval has no schema and therefore no coverage limit — which is its
advantage here and its failure mode in §7, since it also returns something for questions
that should have no answer.

## The part nobody mentions (P5, contradicted)

Every argument for graphs assumes the graph. But it cannot be built without deciding
that "Halcyon Trading Co" and "Halcyon Trading Company" are one node, and that "Meridian
Shipping Ltd" and "Meridian Freight Services" are two. That is a similarity judgement
over surface form: an embedding's job, and one traversal cannot do.

So the architecture is not graph *or* vector. It is **vector for identity, graph for
traversal**.

Sweeping the resolution threshold was expected to show proportional degradation. Instead
it showed an asymmetry:

| threshold | merges | splits | exact answers | false alarms |
|---|---|---|---|---|
| 0.20 | 4 | 0 | 14/16 | **1** |
| 0.40 | 2 | 1 | **16/16** | 0 |
| 0.45–0.60 | 0 | 0 | 16/16 | 0 |
| 0.75 | 0 | 11 | 12/16 | 0 |
| 0.85 | 0 | 20 | 8/16 | 0 |

Three things fall out of that table.

**Splits fail by omission; merges fail by invention.** Across the whole sweep, the
thresholds that merge produced a false alarm. The thresholds that only split produced
none, ever — the worst of them still answers 8 of 16 without asserting anything untrue.
In compliance those are not comparable outcomes: a missing answer gets escalated, a
fabricated one gets acted on.

**At threshold 0.40 the resolver makes three errors and every answer is still exactly
right.** Resolution errors are only visible when they touch a path someone traverses. The
error rate you can measure and the error rate that matters are different quantities, and
the second one depends on the queries.

**Aggregate metrics hide precisely the errors that matter.** At two decimal places, pair
recall and pair specificity read 1.00 across the entire sweep. Four bad merges out of
~550 cross-entity pairs is a specificity of 0.993 — a number that rounds to perfect and
reads as success on a dashboard, while fabricating a sanctions exposure. The damage is
not proportional to the pair count; it is proportional to how *central* the merged node
is.

### The trap that never sprang

The corpus was seeded with "Meridian Shipping Ltd" and "Meridian Freight Services",
expecting a low threshold to fuse them. **No threshold ever does** — their cosine
similarity is 0.269, and by the time the threshold falls that far, greedy clustering has
already placed them elsewhere.

What happens instead is worse and was not designed. "Meridian Freight Services" is fused
with **"Baltic Freight"** — two firms sharing one generic industry word — and Baltic
Freight sits inside the sanctioned ownership chain. Ravenna Textiles buys from the merged
node, the merged node inherits the sanctioned parent's edges, and the graph asserts an
exposure no document supports.

The report prints the merge it measured rather than the one it predicted:
`Experiments.describeMerges` computes the fused pairs at the offending threshold, and a
test asserts the designed trap never fires, so the claim cannot rot back into the
comfortable version.

## Provenance is not the safety property it looks like (P9, contradicted)

Every edge carries the id of the document that asserts it, so every answer arrives with
a chain. This is the graph's most under-rated property and it decides whether a
compliance answer is usable at all — an analyst justifying a frozen payment needs the
four filings, not a confidence score.

```
Ashford Components --SUPPLIES--(inv)--> Meridian Shipping Ltd --SUBSIDIARY_OF-->
  Baltic Freight AG --SUBSIDIARY_OF--> Silverline Holdings --OWNED_BY-->
  Viktor Anisimov --LISTED_ON--> OFAC SDN List
    documents: [0, 1, 2, 3, 4]
```

Verifiable, yes: every one of the 13 steps across the returned paths cites a document
that really does state the relation claimed.

Safer, no. §5's fabricated path had *the same property* — every link a real document,
every edge genuinely asserted. The falsehood was in the node identity, and nothing
downstream of the node can see it.

**A provenance chain is evidence that the edges were asserted. It is not evidence that
the entities were correctly resolved.** The second failure is the one that produces
confident, well-cited, wrong answers, and anyone shipping this needs the resolution
decisions in the audit trail alongside the edges.

## What it adds up to

Retrieval finds text. Traversal derives facts.

- Answer written down → same tool, retrieval is cheaper.
- Answer is a composition → retrieval fails, not gradually and not for want of a better
  encoder.
- Question outside the schema → traversal cannot answer at all.
- And the graph is built by an embedding, so they were never alternatives.

**Vector for identity, graph for traversal** — with a failure mode neither has alone: a
well-cited path between the wrong nodes.
