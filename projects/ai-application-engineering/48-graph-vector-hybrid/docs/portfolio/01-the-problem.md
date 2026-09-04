# 01 — The problem

## The question people actually ask

*"Should we use a knowledge graph or a vector database?"*

It gets asked in architecture reviews as though it has an answer, and it gets answered
with vendor material on both sides. The framing is wrong, and the interesting part is
not that it is wrong but *where* it breaks — which is measurable.

## The domain, and why it was chosen

Sanctions screening over corporate ownership filings.

A compliance analyst has to decide whether a payment may proceed. The relevant question
is: *is any supplier of this company ultimately controlled by a sanctioned person?*

Here are five documents from the corpus:

```
d0  Meridian Shipping Limited is a registered supplier to Ashford Components plc.
d1  MERIDIAN SHIPPING LTD is a wholly owned subsidiary of Baltic Freight AG.
d2  Baltic Freight AG is controlled by Silverline Holdings SA.
d3  Silverline Holdings is beneficially owned by Viktor Anisimov.
d4  V. Anisimov was added to the OFAC SDN List in March 2022.
```

Composed, they say that Ashford Components buys from a company ultimately controlled by
a sanctioned individual.

**No document says that.** No filing in the world would. The five assertions were made
by five parties who never spoke to each other, in different jurisdictions, for different
regulators, years apart. The conclusion exists only as their composition.

This is the property that makes the domain the right test case. It is not a hard
*retrieval* problem — every one of those sentences is short, clear and unambiguous. It
is a problem where **the thing you need to find is not written down anywhere**, and no
improvement to finding written-down things will produce it.

Note also what d0 and d1 do to each other: "Meridian Shipping Limited" and "MERIDIAN
SHIPPING LTD" have to become one node before the chain exists at all. Hold that thought.

## Why the usual comparison is uninformative

Benchmark a "RAG pipeline" against a "knowledge graph" and you are comparing eight
components against four. A measured difference could come from the chunker, the encoder,
the reranker, the reader's prompt, the extractor, the resolver, the query language, or
the store. Every such comparison is really a comparison of two teams' engineering
effort, and it concludes whatever the author needed it to conclude.

Two decisions make this one interpretable:

**One executor, two graphs** (ADR 002). Both systems run the *same* query plan through
the *same* code. They differ in which graph they get: the full extracted graph, or the
subgraph induced by the top-k retrieved documents. So the retriever's reader is
*perfect* — it never hallucinates, never misreads, and composes flawlessly across
everything it retrieved. Every vector number is an upper bound on any RAG pipeline over
this corpus, and every vector failure is a floor.

**Plans are given, not parsed** (ADR 003). Turning the question into a traversal is a
language problem, and a parser's errors would be indistinguishable from a substrate's
limits. Both systems get the correct plan, handed over identically.

The comparison then answers exactly one question: *given a correct plan and a perfect
reader, what can each substrate do?*

## What the report is actually arguing

Three claims, each measured, each with a prediction registered before the measurement:

1. **Retrieval does not fail gradually on composition — it falls off a cliff**, and the
   cliff is between one hop and two. More retrieval does not fix it, because the problem
   is retrieving a *conjunction* of passages that do not individually resemble the
   question.

2. **The graph's real limit is schema coverage, not paraphrase.** Its extractor knows
   five relations; the corpus contains facts that are none of them. That limit is fixed
   before the questions are known, and no amount of query cleverness moves it.

3. **The graph is built by an embedding.** You cannot build it without deciding that
   "Meridian Shipping Limited" and "MERIDIAN SHIPPING LTD" are the same node. That is a
   similarity judgement over surface form — precisely what an embedding does, and
   precisely what traversal cannot.

The third claim is why the first two do not add up to "use graphs". They add up to
something more specific: **vector for identity, graph for traversal**, with a failure
mode neither substrate has on its own — a well-cited path between the wrong nodes.

## What it cost to find that out

Nine predictions were registered in code before the measurements that settle them.
**Five were contradicted**, including three that were the reason the corresponding
experiment existed. The design of the corpus contains a deliberate trap that never
sprang, and a different, worse failure that was never designed at all.

Those are in [03-the-argument.md](03-the-argument.md) and
[04-what-the-tests-caught.md](04-what-the-tests-caught.md).
