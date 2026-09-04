# Known limitations

Everything here is a real constraint on what this repository's numbers mean.
None of it is hidden in a footnote of the report; the report states each one
where it applies. This file collects them so a reader can decide whether to
trust a claim before reading it.

## The corpus is generated, so absolute scores mean nothing

The corpus is rendered from declared facts (ADR 0001), which is what makes the
labels complete and span-level. It is not natural text. Its vocabulary is
smaller than a real corpus's, its sentence shapes are more uniform, and its
distractors were written by a generator that knows what a distractor should
look like.

**Consequence:** an nDCG of 0.704 here is not comparable to an nDCG of 0.704 on
MS MARCO or on your documents. Only differences between configurations measured
on identical material are meaningful, and even those transfer only as far as
the generator's assumptions hold.

**A concrete instance of the limit, from section 6:** latent semantic indexing
fails on the `implicit` query class partly because the generator had no reason
to place "largest accounts" and "Enterprise" in the same chunk. A human-written
corpus would sometimes do so, and LSA might then learn the association. The
report says this rather than claiming LSA is useless on implicit queries.

## No neural retrieval, and it is not a small gap

There are no embedding models, no cross-encoders, and no API calls anywhere in
this repository. The environment has no model weights and no network access to
obtain them.

The "distributional" retriever is **latent semantic indexing** — truncated SVD
over the term-document matrix. That is a genuine distributional method with a
long pedigree, and it is the correct classical stand-in, but it is not a
sentence transformer. The `implicit` query class is exactly where a modern
bi-encoder would be expected to do best, and this grid has no member that
represents one.

The `fitted` reranker is a **linear model over seven hand-designed lexical
features** fitted by coordinate ascent. It is called "cross-encoder-style"
because it scores query-document pairs jointly, but it shares nothing else with
a transformer cross-encoder.

**What survives the gap:** the four structural findings do not depend on which
retriever is used. Reachability bounds every retriever equally. Document-level
labels decorrelate the judgement for every retriever equally. The minimum
detectable effect is a property of the query set. A reranker cannot exceed pool
recall regardless of its architecture.

**What does not survive:** the specific ordering of the five retrievers, and
the `implicit` class result, which would look different with a bi-encoder.

## Cost is counted, not timed

Per ADR 0004, cost is multiply-accumulate operations, not milliseconds. This
makes the report reproducible (`test.ps1` hashes it against a fresh run) and
makes the comparison about algorithms rather than about how many attribute
lookups are in a Python loop.

**Consequence:** the Pareto frontier in section 9 is a frontier in algorithmic
work, not a latency curve. A reader who cares about p99 has to do their own
translation, and costs across different stage types (posting-list entries
versus dense dot products) are not commensurable. The report never adds them.

## The statistics assume a parametric test

Section 3's primary test is a paired Student's t, chosen over a permutation
test for the reason set out in ADR 0005 and section 3a: at B = 4,000 the
permutation test's resolution floor (2.50e-04) sits above Holm's strictest
threshold (8.40e-05), so the corrected column was forced to zero by arithmetic.

The t-test buys resolution at the cost of a distributional assumption.
Per-query nDCG differences are zero-inflated with heavy tails — far from
normal. The defence is that n = 273 per comparison and the statistic is a mean
of bounded quantities, so the CLT applies to the *sampling distribution of the
mean* even though the differences themselves are not normal, and the observed
p-values (down to 3.56e-78) are nowhere near the threshold.

**Consequence:** a comparison sitting exactly at the boundary should not be
trusted on the strength of the t-test alone. None of the reported conclusions
rests on such a comparison.

## The minimum detectable effect binds this report too

Section 4 computes 0.046 nDCG as the smallest difference 273 queries can
resolve at 80% power. **Twelve of the 35 configurations sit within 0.046 of the
leader.** This corpus cannot order them and neither can this report — section
10 says so in those words rather than declaring a winner.

Anything in the report described as a difference smaller than 0.046 (the
oracle-routing gain of +0.005, the two-point Pareto frontier spanning 0.011,
the mean tuning optimism of +0.016) is *below the resolution of the
instrument*. Those numbers are reported because their smallness is the finding.

## The generated-corpus hazard is mitigated, not eliminated

Because relevance is a set operation on declared spans, any correct answer the
generator produces *outside* a declared placement is an unlabelled positive —
the exact pooling bias the design exists to avoid.

Three invariants guard it: `assert_values_are_distinctive`,
`assert_no_leaked_values`, `assert_queries_do_not_quote_answers`. There is a
negative test proving the leak-check actually fires on a planted leak.

**What remains:** the guards test the values and the queries. They cannot prove
that no generated *sentence* happens to constitute a complete answer by
paraphrase. Defect #1 in `docs/portfolio/04-bugs-the-experiment-found.md` is a
case that got through until it was caught by hand.

## Chunker x retriever is one factor, not two

IDF is computed over chunks. Changing the chunker therefore changes every IDF
weight, so the retriever's behaviour is not independent of the chunking.

This is deliberate — it is what a real pipeline does — but it means the grid
cannot be read as a two-factor design with separable main effects. There is no
"the effect of BM25" independent of the chunker it indexed.

## Query classes are declared, not discovered

The five classes (`lexical`, `paraphrase`, `implicit`, `unqualified`, `multi`)
are properties of how each query was generated, not a clustering of observed
behaviour. That makes them clean, and it also means they are the generator's
theory of how queries differ rather than an empirical one. A real query log
would have a different and messier partition.

## Class sizes are unequal and two are small

78 / 78 / 72 / 30 / 15. The `multi` class has 15 queries; its per-class figures
have wide intervals that the report does not draw. The +0.024 oracle gain on
`multi` is the least trustworthy number in section 6, and the section's
conclusion does not lean on it.

## What is not tested

- **Ingestion.** Documents arrive as strings from a generator. No PDF
  extraction, no HTML, no OCR — and layout extraction is a large real-world
  source of exactly the severance this lab measures.
- **Updates.** The index is built once. No incremental indexing, no deletion,
  no staleness.
- **Scale.** The largest index is 5,245 chunks. Nothing here says anything
  about behaviour at 10^7.
- **Generation.** The lab stops at retrieval. `misleading@1` is a proxy for
  "the generator will produce a confident wrong answer" — a well-founded one,
  but a proxy. No generator is run.
- **Multilingual, code, and tabular retrieval** beyond the one table layout.
