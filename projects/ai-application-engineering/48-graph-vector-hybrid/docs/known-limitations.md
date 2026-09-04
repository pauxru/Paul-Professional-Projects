# Known limitations

What this project does not show. Written so that nobody has to discover it by reading
the source.

## 1. The corpus is synthetic and small

Eighty documents, 26 entities, 50 relations, 19 questions. It is large enough to make
the mechanisms visible and far too small to support a claim about absolute performance.

Every number here is about *shape*: that the multi-hop cliff exists, that merges and
splits fail asymmetrically, that schema coverage rather than paraphrase is the graph's
real limit. None of them is a benchmark score, and none should be quoted as one.

The corpus is small in a specific direction that flatters retrieval: at k=80 the
retriever has the entire corpus, so premise recall is trivially complete. In a real
index of a million filings, the k needed for the multi-hop questions is not 40-of-80,
it is unbounded. §3 makes this point but cannot measure it here.

## 2. The reader is perfect, so the RAG numbers are upper bounds

The vector system's "reader" is the same executor the graph uses. It never hallucinates,
never misreads, and composes flawlessly. Real LLM readers do all three.

This makes every vector number an upper bound and every vector failure a floor -- the
useful direction -- but it also means the report **cannot say anything about
hallucination**, which is the failure mode most people care about in RAG. §7's finding
that the vector system produces no false alarms on negative questions is a property of
the executor and **does not transfer** to a generative reader. The report says so;
this file repeats it because it is the most quotable-out-of-context result in it.

## 3. Natural language to plan is not measured

Plans are written by hand and handed to both systems (ADR 003). Translating a question
into a traversal is a large, real part of a production system, and this project
contributes nothing to it.

The results are conditional: *given a correct plan*, here is what each substrate can do.
A system that cannot reliably produce the plan will not achieve the graph numbers here.

## 4. The embedding is lexical

Character 4-gram TF-IDF, not a sentence encoder. ADR 001 argues, with two independent
measurements, that this does not affect the multi-hop conclusions -- and quantifies the
paraphrase penalty it *does* cause (mean k of 4.5 versus 2.0 for literal lookups).

What cannot be checked here: whether a neural encoder would show a *qualitatively*
different pattern on paraphrase-heavy workloads. §8's prediction is the thing to re-run
if an encoder becomes available.

## 5. Extraction is not modelled

The corpus declares the triple for each factual document. A real system extracts triples
with an error rate, and those errors compound with the resolution errors §5 measures.

This is a deliberate scope boundary -- an extractor's errors would be indistinguishable
from resolution errors in the results -- but it means the graph side is measured with
one of its two build-time error sources switched off. The real-world graph is worse than
the one measured here, in a way this report does not quantify.

`Systems.surfaceOf` recovers the surface form by finding which declared variant the
sentence actually contains, so the *graph is built from what the text says* rather than
from the answer key. That keeps the resolution measurement honest without adding a
named-entity recogniser whose errors would confound it.

## 6. One resolution algorithm, one corpus

Single-pass greedy clustering. A better clusterer moves the threshold at which each
error appears; ADR 004 argues it does not remove the trade-off. That argument is
reasoning, not measurement -- no second algorithm was tried.

Relatedly, the specific merge that causes the false alarm ("Baltic Freight" with
"Meridian Freight Services") is partly an artefact of greedy assignment order. The
*class* of failure -- a merge fabricating a well-cited path -- is general. The particular
pair is not.

## 7. No performance, cost, or scale measurement

No latency, no index size, no traversal cost, no comparison of what k=40 costs against
what a graph query costs. In production these often decide the architecture, and this
report is silent on all of them.

The BFS is brute-force, the retrieval is brute-force cosine over an array, and neither
is meant to be representative of a real system's cost profile.

## 8. The negative-question sample is two

§7 rests on two questions. It is enough to demonstrate the mechanism -- that the
executor's silence is a closed-world statement while the retriever's is "nothing similar
was in the top k" -- and nowhere near enough to characterise false-alarm rates.

## 9. The report is one seed

Everything is deterministic and there is no randomness to vary, which makes the results
reproducible but means there are no confidence intervals. With 19 questions, a single
question changing category would move several percentages visibly. Read the tables as
existence proofs of a mechanism, not as estimates.
