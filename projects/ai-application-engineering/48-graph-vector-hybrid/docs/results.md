# Graph traversal and vector retrieval are not competing answers to one question

They answer different questions, and the difference is not a matter of quality. A retriever returns text that exists. A traversal derives facts that no text states. Everything below measures where that boundary falls and what it costs to cross it.

**Method.** Both systems execute the *same* query plan through the *same* executor. They differ in one thing: which graph they are handed. The graph system gets the full extracted graph; the vector system gets the subgraph induced by its top-k retrieved documents. This makes the retriever's reader perfect -- it never hallucinates, never misreads a passage, and composes facts flawlessly across everything it was given. Every number reported for the vector system is therefore an **upper bound on what any RAG pipeline over this corpus could achieve**, and the failures below are floors rather than artefacts of a weak baseline.

The embedding is a character 4-gram TF-IDF model, not a neural encoder. What that changes, and what it demonstrably does not, is measured in section 8 and argued in `docs/adr/001-lexical-embeddings.md`.


## 1. The corpus, and the fact it does not contain

Eighty documents about corporate ownership and supply relationships. Fifty state exactly one relation each; thirty are plausible, topical, and state nothing -- they exist so that top-k retrieval has something confident and wrong to return, which is the realistic failure. An empty result is obvious. A confident irrelevant one is not.

| doc | text |
|---|---|
| d0 | Meridian Shipping Limited is a registered supplier to Ashford Components plc. |
| d1 | MERIDIAN SHIPPING LTD is a wholly owned subsidiary of Baltic Freight AG. |
| d2 | Baltic Freight AG is controlled by Silverline Holdings SA. |
| d3 | Silverline Holdings is beneficially owned by Viktor Anisimov. |
| d4 | V. Anisimov was added to the OFAC SDN List in March 2022. |

Those five documents, composed, say that Ashford Components buys from a company ultimately controlled by a sanctioned individual. **No document in the corpus says that**, because no filing in the world would. The fact exists only as the composition of five separate assertions made by five parties who never spoke to each other. That is what a multi-hop question is, and it is why retrieval quality is not the relevant axis.

The graph extracted from the fifty factual documents has 26 nodes and 50 edges across five relation types. The retriever indexes all eighty documents, including the distractors, because a real index does not know which is which.

> **Predicted (P1)** -- On single-hop questions -- where the answer is stated verbatim in one document -- the two systems will be indistinguishable, because retrieval only has to find one thing.

| id | question | vector k=10 exact | graph exact |
|---|---|---|---|
| Q1 | Who is a director of Meridian Shipping? | yes | yes |
| Q2 | Where is Baltic Freight AG registered? | yes | yes |
| Q3 | Who owns Silverline Holdings? | yes | yes |
| Q4 | Which company supplies Pemberton Metals? | yes | yes |

> **Held (P1)** -- Indistinguishable: 4 of 4 each. Retrieval is a perfectly good way to answer a question whose answer is written down, and nothing in this report argues otherwise.


## 2. Accuracy against hop distance

The same comparison, grouped by how many edges the answer path needs.

> **Predicted (P2)** -- Vector accuracy will fall off gradually with hop count -- worse at three hops than at two, but still finding some answers, because a large enough k will often happen to contain the whole chain.

| hops | questions | vector k=10 exact | graph exact |
|---|---|---|---|
| 1 | 7 | 7/7 | 7/7 |
| 2 | 3 | 1/3 | 3/3 |
| 3 | 2 | 0/2 | 2/2 |
| 4 | 2 | 1/2 | 2/2 |

> **Contradicted (P2)** -- There is no gradual fall-off. Vector answers 2 of the 7 questions needing two or more hops. The transition is not from good to poor; it is from working to not working, and it happens between one hop and two. Section 3 measures why.


## 3. Why more retrieval does not help: the conjunction problem

A single-hop question needs one document. A four-hop question needs all nine, *simultaneously*, inside the same top-k window. Retrieval ranks documents by similarity to the question, and the documents in the middle of a chain do not resemble the question -- "Baltic Freight AG is controlled by Silverline Holdings SA" shares almost nothing with "which suppliers of Ashford Components are ultimately controlled by a sanctioned person". Those documents are relevant by composition, not by similarity, and similarity is the only thing the index knows.

> **Predicted (P3)** -- Recall of the full premise set will improve steadily with k, so a sufficiently large k recovers multi-hop performance. This is the standard remedy and the reason production systems ship k=50.

| id | hops | premises | k=3 | k=5 | k=10 | k=20 | k=40 | k=80 |
|---|---|---|---|---|---|---|---|---|
| Q5 | 1 | 2 | 1/2 | **all** | **all** | **all** | **all** | **all** |
| Q7 | 2 | 2 | 0/2 | 1/2 | 1/2 | 1/2 | 1/2 | **all** |
| Q8 | 2 | 2 | 0/2 | 1/2 | **all** | **all** | **all** | **all** |
| Q9 | 2 | 2 | 1/2 | 1/2 | 1/2 | **all** | **all** | **all** |
| Q10 | 3 | 3 | 2/3 | 2/3 | 2/3 | 2/3 | 2/3 | **all** |
| Q11 | 3 | 3 | 1/3 | 1/3 | 2/3 | 2/3 | 2/3 | **all** |
| Q12 | 4 | 9 | 2/9 | 3/9 | 4/9 | 4/9 | 6/9 | **all** |
| Q13 | 1 | 3 | **all** | **all** | **all** | **all** | **all** | **all** |
| Q14 | 1 | 5 | 3/5 | **all** | **all** | **all** | **all** | **all** |

> **Contradicted (P3)** -- It does not recover. Of the 9 questions needing more than one premise, 5 have their full premise set inside the top 40 of an 80-document corpus -- half the corpus, a k no production system would run. At k=80, which is the entire corpus, 9 do, and at that point the retriever has stopped being a retriever. The premises that stay missing are the middle links, which is exactly what the similarity argument predicts: they are the ones with no lexical relationship to the question.

This is the mechanism behind a familiar production experience -- raising k improves the easy questions, does nothing for the hard ones, and increases cost and latency monotonically. The hard questions are not hard because retrieval is imprecise. They are hard because their answer requires a conjunction, and ranking optimises each item independently.


## 4. Isolating the cause: an oracle retriever

If the failure is retrieval of the conjunction, then handing the retriever exactly the right documents should fix it completely. If something else is wrong -- reasoning, composition, the plan -- it should not.

> **Predicted (P4)** -- Given an oracle that returns precisely the premise documents and nothing else, the vector system will match the graph exactly. The entire multi-hop deficit is retrieval of the conjunction and none of it is reasoning.

Every question the oracle was given, it answered exactly.

> **Held (P4)** -- Confirmed, and this is the load-bearing result of the report. With perfect retrieval the vector system scores 16/16 -- identical to the graph. The reasoning machinery is shared and it works. **The multi-hop deficit is entirely the probability of retrieving a conjunction, and no improvement to the encoder changes the shape of that problem**: a better encoder ranks each document better; it does not make the middle of a chain resemble the question.


## 5. The part nobody mentions: the graph is built by an embedding

A graph over messy filings cannot be built without deciding that "Halcyon Trading Co" and "Halcyon Trading Company" are one node, and that "Meridian Shipping Ltd" and "Meridian Freight Services" are two. That is a similarity judgement over surface form. It is what embeddings are for, and it is what traversal cannot do.

So the architecture these measurements support is not graph *or* vector. It is **vector for identity, graph for traversal**: the embedding decides what a node is, the graph decides what follows from it.

> **Predicted (P5)** -- Resolution errors will degrade answers roughly in proportion to the error rate -- a few bad merges will cost a few answers.

| threshold | merge errors | split errors | pair recall | pair specificity | exact answers | false alarms |
|---|---|---|---|---|---|---|
| 0.20 | 4 | 0 | 1.000 | 0.996 | 14/16 | 1 |
| 0.30 | 3 | 3 | 0.864 | 0.997 | 14/16 | 0 |
| 0.40 | 2 | 1 | 0.955 | 0.998 | 16/16 | 0 |
| 0.45 | 0 | 0 | 1.000 | 1.000 | 16/16 | 0 |
| 0.55 | 0 | 0 | 1.000 | 1.000 | 16/16 | 0 |
| 0.60 | 0 | 0 | 1.000 | 1.000 | 16/16 | 0 |
| 0.65 | 0 | 4 | 0.818 | 1.000 | 14/16 | 0 |
| 0.75 | 0 | 11 | 0.500 | 1.000 | 12/16 | 0 |
| 0.85 | 0 | 20 | 0.091 | 1.000 | 8/16 | 0 |

The two ratio columns are printed to three decimals because at two they both read 1.00 across the whole sweep. There are 1013 pairs that should be apart, so four bad merges is a specificity of 0.993 -- a number that rounds to perfect and reads as success on a dashboard. **Aggregate resolution metrics hide exactly the errors that matter**, because the damage is not proportional to the pair count; it is proportional to how central the merged node is.

> **Contradicted (P5)** -- Not proportional, and -- far more importantly -- **not symmetric**. Across the whole sweep the thresholds that merge produced 1 false alarm(s); the thresholds that only split produced 0. Splits fail by omission: the graph comes apart, fewer questions are answerable, and the worst split-only threshold still answers 8 of 16 without ever asserting something untrue. Merges fail by invention. **The error that looks smaller on every dashboard is the one that produces a wrong answer rather than no answer**, and in compliance those are not comparable outcomes -- a missing answer gets escalated, a fabricated one gets acted on.

At threshold 0.40 the resolver makes 2 merge and 1 split errors and every one of the 16 answers is still exactly right. Resolution errors are only visible when they touch a path somebody traverses, so the error rate you can measure and the error rate that matters are different quantities, and the second one depends on the queries.

There is a window of 3 of 9 sampled thresholds where resolution is exactly right, and the uncomfortable part is that **you cannot locate that window without ground truth** -- which is the thing you were building the graph to obtain. In production this is why entity resolution is a labelled-data problem wearing an unsupervised costume.

The specific damage is worth naming, and it is not the damage the corpus was built to bait. Two entities were planted with confusable names -- "Meridian Shipping Ltd" and "Meridian Freight Services" -- on the expectation that a low threshold would fuse them. **No threshold ever does.** What happens instead: at threshold 0.20, "Baltic Freight" is fused with "Meridian Freight"; "Baltic Freight" is fused with "Meridian Freight Services"; "Baltic Freight AG" is fused with "Meridian Freight"; "Baltic Freight AG" is fused with "Meridian Freight Services".

That is a more uncomfortable result than the one that was designed. The merge that does the damage is between two names sharing a single generic industry word, and it happens because greedy clustering assigns each surface form to whichever cluster already exists and scores highest -- so the fused pair depends on processing order and on which names arrived first, not on the pair being especially similar. Ravenna Textiles buys from the merged node, the merged node inherits the sanctioned parent's edges, and the graph asserts a sanctions exposure that no document supports, with a provenance chain in which **every cited document is real and every edge was genuinely asserted**. The falsehood is in the node, and nothing downstream of the node can see it.


## 6. Where the graph loses, and it is not paraphrase

A comparison that only asks questions the graph was built to answer is not a comparison. The extractor recognises five relation types. The corpus, like every real corpus, contains facts that are none of them.

> **Predicted (P6)** -- The graph will lose on paraphrased questions, where the wording differs from the stored relation names -- the usual argument for embeddings.

| id | question | vector k=5 | graph |
|---|---|---|---|
| Q17 | Who maintains the OFAC SDN List? | **found** | **not in schema** |
| Q18 | Why are Cyprus and Malta registries hard to work with? | **found** | **not in schema** |
| Q19 | How many vessels does Kestrel Maritime operate? | **found** | **not in schema** |

> **Contradicted (P6)** -- Paraphrase is not where the graph loses -- it scores 2/2 against the retriever's 2/2, because once a question has been turned into a plan its wording is irrelevant. The graph loses somewhere more fundamental: **3 of 3 open questions have no representation in its schema at all.** "Who maintains the OFAC SDN List?" is answered by one sentence in the corpus and by no edge in the graph, because MAINTAINS was not a relation the extractor knew about. The retriever finds 3 of 3 in its top 5.

This is the real trade and it is structural. A graph's coverage is bounded by a schema fixed before the questions were known. Retrieval has no schema and therefore no coverage limit -- it returns something for any question, which is simultaneously its advantage here and its failure mode in section 7.


## 7. Saying nothing

Two questions have the empty set as their correct answer. In compliance this is the majority case and the only one with a deadline: the operational question is not "who is sanctioned" but "may this shipment proceed".

> **Predicted (P7)** -- Both systems will handle the negatives, since neither has any incentive to invent an answer -- the executor returns only nodes it actually reached.

| id | question | vector k=10 | graph |
|---|---|---|---|
| Q15 | Which suppliers of Ravenna Textiles are controlled by a sanctioned person? | (nothing) | (nothing) |
| Q16 | Is Meridian Freight Services owned by Silverline Holdings? | (nothing) | (nothing) |

> **Held (P7)** -- Both stay silent, and the reason is worth stating because it is a design choice rather than a property of either substrate: the shared executor returns only nodes reached by a real edge. A generative reader over the same retrieved passages has no such constraint. It sees Meridian Freight Services, Silverline Holdings and the word sanctioned inside one window, and the passages that would rule out a connection are not there to rule it out. Q16 exists to make that concrete: the corpus never says Meridian Freight is *not* owned by Silverline. **Absence is not retrievable.**

The asymmetry that matters is not who gets the negative right today. It is that the graph's silence is a *closed-world* statement -- no path exists in the extracted graph -- which is checkable, and wrong in knowable ways. The retriever's silence means only that nothing similar appeared in the top k.


## 8. What a better encoder would and would not change

The embedding here is character 4-gram TF-IDF. It is a real vector space with a real metric, and it is weaker than a sentence encoder at exactly one thing: recognising that two differently worded sentences mean the same. That weakness has a measurable footprint, and it is worth locating precisely rather than waving at.

> **Predicted (P8)** -- The lexical model's disadvantage will show up as poor ranking of premise documents for paraphrased questions, and nowhere else that matters.

| id | kind | hops | premises | k needed for full recall |
|---|---|---|---|---|
| Q1 | LOOKUP | 1 | 1 | 1 |
| Q2 | LOOKUP | 1 | 1 | 1 |
| Q3 | LOOKUP | 1 | 1 | 5 |
| Q4 | LOOKUP | 1 | 1 | 1 |
| Q5 | PARAPHRASE | 1 | 2 | 5 |
| Q6 | PARAPHRASE | 1 | 1 | 4 |
| Q7 | MULTIHOP | 2 | 2 | 68 |
| Q8 | MULTIHOP | 2 | 2 | 6 |
| Q9 | MULTIHOP | 2 | 2 | 20 |
| Q10 | MULTIHOP | 3 | 3 | 54 |
| Q11 | MULTIHOP | 3 | 3 | 46 |
| Q12 | MULTIHOP | 4 | 9 | 60 |
| Q13 | AGGREGATE | 1 | 3 | 3 |
| Q14 | AGGREGATE | 1 | 5 | 5 |
| Q17 | OPEN | 1 | 1 | 1 |
| Q18 | OPEN | 1 | 1 | 1 |
| Q19 | OPEN | 1 | 1 | 2 |

The last column is the k a perfect-recall retriever would need for that question. A better encoder moves those numbers down. It does not change which of them are large: the large ones belong to multi-hop questions, and they are large because the middle of a chain has no lexical *or* semantic relationship to the question. "Baltic Freight AG is controlled by Silverline Holdings SA" is not about Ashford Components under any encoder, because it is not about Ashford Components.

> **Held (P8)** -- Mean k needed for full premise recall: literal lookups 2.0, paraphrased 4.5, multi-hop 42.3. The paraphrase penalty is real and is exactly what a neural encoder fixes. It is also far smaller than the multi-hop penalty, which no encoder fixes. Section 4 settled this independently: with perfect retrieval the vector system matches the graph, so the encoder is not the binding constraint on anything measured here.


## 9. Provenance, and what it is not evidence of

Every edge carries the id of the document that asserts it, so every answer comes back with the chain that produced it. This is the graph's most under-rated property and it decides whether a compliance answer is usable at all: an analyst who must justify freezing a payment needs the four filings, not a confidence score.

> **Predicted (P9)** -- Provenance makes graph answers verifiable, which makes the graph the safer substrate for high-stakes questions.

Q12 -- *which suppliers of Ashford Components are ultimately controlled by a sanctioned person* -- returns 3 answers, each with a path:

```
Ashford Components --SUPPLIES--(inv)--> Kestrel Maritime --OWNED_BY--> Silverline Holdings --OWNED_BY--> Viktor Anisimov --LISTED_ON--> OFAC SDN List
    documents: [5, 6, 3, 4]
Ashford Components --SUPPLIES--(inv)--> Meridian Shipping Ltd --SUBSIDIARY_OF--> Baltic Freight AG --SUBSIDIARY_OF--> Silverline Holdings --OWNED_BY--> Viktor Anisimov --LISTED_ON--> OFAC SDN List
    documents: [0, 1, 2, 3, 4]
Ashford Components --SUPPLIES--(inv)--> Orion Chartering --SUBSIDIARY_OF--> Silverline Holdings --OWNED_BY--> Viktor Anisimov --LISTED_ON--> OFAC SDN List
    documents: [35, 43, 3, 4]
```

> **Contradicted (P9)** -- Verifiable, yes -- all 13 steps across those paths cite documents that really do state the relation claimed. Safer, not necessarily. Section 5 produced a fabricated path whose every link was a real document; the falsehood was in the node identity, not in any edge. **A provenance chain is evidence that the edges were asserted. It is not evidence that the entities were correctly resolved**, and the second failure is the one that produces confident, well-cited, wrong answers. Anyone shipping this needs the resolution decisions in the audit trail alongside the edges.


## 10. The whole thing in one paragraph

Retrieval finds text. Traversal derives facts. On questions whose answer is written down, they are the same tool and retrieval is cheaper. On questions whose answer is a composition, retrieval fails -- not gradually, and not for want of a better encoder, but because it must retrieve a conjunction of passages that do not individually resemble the question. On questions outside the graph's schema, traversal cannot answer at all. And the graph itself is built by an embedding making similarity judgements about identity, so the two techniques were never alternatives: **the working architecture is vector for identity and graph for traversal, and the failure mode of the combination is a well-cited path between the wrong nodes.**


## Scoreboard

| id | prediction | outcome |
|---|---|---|
| P1 | On single-hop questions -- where the answer is stated verbatim in one document -- the two systems will be indistinguishable, because retrieval only has to find one thing. | held |
| P2 | Vector accuracy will fall off gradually with hop count -- worse at three hops than at two, but still finding some answers, because a large enough k will often happen to contain the whole chain. | **contradicted** |
| P3 | Recall of the full premise set will improve steadily with k, so a sufficiently large k recovers multi-hop performance. This is the standard remedy and the reason production systems ship k=50. | **contradicted** |
| P4 | Given an oracle that returns precisely the premise documents and nothing else, the vector system will match the graph exactly. The entire multi-hop deficit is retrieval of the conjunction and none of it is reasoning. | held |
| P5 | Resolution errors will degrade answers roughly in proportion to the error rate -- a few bad merges will cost a few answers. | **contradicted** |
| P6 | The graph will lose on paraphrased questions, where the wording differs from the stored relation names -- the usual argument for embeddings. | **contradicted** |
| P7 | Both systems will handle the negatives, since neither has any incentive to invent an answer -- the executor returns only nodes it actually reached. | held |
| P8 | The lexical model's disadvantage will show up as poor ranking of premise documents for paraphrased questions, and nowhere else that matters. | held |
| P9 | Provenance makes graph answers verifiable, which makes the graph the safer substrate for high-stakes questions. | **contradicted** |

9 predictions registered before measurement; 4 held, 5 contradicted.
