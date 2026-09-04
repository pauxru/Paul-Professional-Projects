# Retrieval Quality Lab

An offline, dependency-light instrument for answering the question a RAG
evaluation is usually assumed to answer and almost never does: **when this
pipeline fails to find an answer, which stage lost it?**

It runs 7 chunkers x 5 retrievers x 4 rerankers over 273 queries against a
generated corpus whose relevance labels are complete by construction, and it
writes [`docs/results.md`](docs/results.md) — an 11-section report in which
every finding is preceded by a prediction that was written *before* the
corresponding measurement was read.

Of 12 predictions, **7 held and 5 did not.** The five that failed are the
interesting ones, and each is discussed where it appears rather than quietly
rewritten.

```powershell
.\demo.ps1            # run it, print the report
.\demo.ps1 -Save      # run it, overwrite docs/results.md
.\demo.ps1 -Section 3a
.\test.ps1            # 366 tests + a byte-identity check on the report
```

No API keys. No network. No model weights. Python 3.12 and NumPy, roughly 40
seconds end to end.

---

## Why this exists

Retrieval evaluations report a number and a ranking, and readers draw
conclusions from them that the number cannot support. Five of those unsupported
claims, and what this lab does about each:

| The claim | Why it usually fails | Where it is measured |
|---|---|---|
| "the labels are complete" | Pooled judgements treat unjudged as irrelevant, and the bias is largest for the least conventional system | corpus is *generated from declared facts*, so labels are a set operation on character offsets (§0, ADR 0001) |
| "these labels can compare chunkers" | Assessors label documents; chunkers produce fragments; the fragment label is inherited | §2 measures the cost on identical rankings |
| "this difference is real" | The minimum detectable effect is almost never reported | §4 — it is **0.046 nDCG** here, and 12 of 35 configurations are inside it |
| "the aggregate is what to optimise" | One query class in five can be at the floor | §6 — the whole grid tops out at 0.393 on `implicit` |
| "the metric captures what goes wrong" | nDCG asks "did the right thing appear", never "did a convincing wrong thing appear instead" | §8 — `misleading@1` reaches 5.5% |

## The four findings worth carrying away

**1. Severance, not loss, is how chunkers destroy answers.** Across the grid,
195 placements were severed from the heading that gives them meaning and only 8
were lost outright. The controlled pair proves it isn't about size:
`structural_240` and `structural_prefixed_240` produce **identical chunk
boundaries** and differ only in whether the heading path is repeated into the
chunk body. Severance goes 45 → 0; on the table layout, intact placements go
0/39 → 39/39. (§1a)

**2. Reachability is a ceiling, not an objective.** `sentence_window_1` reaches
1.000 of targets — perfect — and has the **worst** end-to-end score in the
grid. It didn't remove the loss, it moved it: chunker loss 0.000, retriever
loss 0.298. Splitting into 5,245 units gave every answer a home and gave the
retriever sixteen times more places to look. *The stage you measure is the
stage you fix, and fixing it in isolation relocates the failure to a stage you
were not measuring.* (§1)

**3. Document-level labels don't inflate chunker scores — they decorrelate
them.** The obvious objection is that superset labels flatter everything. They
don't, because nDCG normalises by an ideal that grows with the relevant set: 32
queries score higher, 158 lower, correlation 0.885. An evaluation with
document-level labels is not a weak instrument for comparing chunkers, it is
*not an instrument*, and it will report a confident ordering anyway. (§2)

**4. A permutation test nested in a family-wise correction can be
arithmetically incapable of significance.** This report shipped a "0 significant
after correction" table in an earlier revision. At B = 4,000 a permutation test
cannot report p below 1/(B+1) = 2.50e-04; Holm's strictest threshold over 595
comparisons is 8.40e-05. **No comparison could clear it regardless of effect
size.** The failure is invisible because "nothing was significant" is also what
an honest null experiment looks like. On the identical differences, a paired t
finds 491. (§3a, ADR 0005)

> **Check `1/(B+1) < alpha/m` before nesting a randomisation test inside a
> family-wise correction.**

## How the attribution works

For every (query, target) pair exactly one stage is responsible, assigned by
testing in pipeline order:

- **chunker** — no chunk anywhere covers the target;
- **retriever** — a covering chunk exists but none reached the pool (depth 50);
- **ranker** — a covering chunk reached the pool but not the top 10;
- **served** — a covering chunk is in the top k.

The four sum to one by construction, so a defect doesn't produce a plausible
split — it produces a row that doesn't sum to one, and a test asserts none
does.

That only works because relevance is exact. The corpus is **rendered from
structured facts**: a `Fact` is a topic plus a qualifier ("Enterprise", "eu-central"),
a value is rendered into six document layouts, and each rendering records the
character span of the sentence stating the value plus the spans of the headings
it depends on. A chunk answers a query iff one chunk contains **every** required
span. The negatives are as trustworthy as the positives — which is the
precondition for the harm metrics existing at all.

Deliberately, a value is rendered *without* its qualifier; the qualifier lives
in a heading. A chunk with the sentence but not the heading has kept the answer
and thrown away its meaning.

## What is in the grid

**Chunkers (7):** `fixed_120`, `fixed_240`, `overlap_240_120`, `recursive_240`,
`structural_240`, `structural_prefixed_240`, `sentence_window_1`.

**Retrievers (5):** BM25, TF-IDF cosine, LSA (truncated SVD), reciprocal rank
fusion, score blend.

**Rerankers (4):** none, MMR, a hand-weighted linear model over seven lexical
features, and the same model fitted by coordinate ascent — reported held-out,
with the fit-on-the-reported-half number beside it so the optimism is visible
(§5: mean +0.016, worst +0.091).

**Metrics:** nDCG@10, MRR, recall@k, pool recall — plus `misleading@1`,
`misleading@10` and `unanswered@10`, which no leaderboard reports and which
are where the operational risk lives (ADR 0002).

**Query classes (5):** `lexical`, `paraphrase`, `implicit`, `unqualified`,
`multi`.

## Layout

```
rqlab/
  facts.py        declared facts, qualifiers, and the corpus invariants
  corpus.py       six layout builders; Placement spans; the query set
  distractors.py  near-miss documents, with a leak check
  text.py         one analyser for the whole pipeline (ADR 0003)
  chunking.py     the seven chunkers; token-aligned span arithmetic
  coverage.py     span containment, reachability, placement survival
  indexes.py      BM25 / TF-IDF / LSA / RRF / blend, each with an op counter
  rerank.py       MMR and the seven-feature linear model
  metrics.py      nDCG, MRR, recall, and the harm counters
  attribution.py  the exclusive-and-exhaustive stage ladder
  stats.py        Holm, paired t via the incomplete beta, MDE, and the
                  resolution arithmetic from ADR 0005
  report.py       the expect/found DSL: found() without expect() raises
  experiment.py   grid execution
run_lab.py        writes docs/results.md
tests/            366 tests
```

`report.py` enforces the discipline mechanically. A section must call
`expect(...)` before `found(...)`; calling `found` twice, or rendering with an
open prediction, raises. That is why the report can honestly say five
predictions were wrong — there was no way to quietly delete them.

## Documents

- [`docs/results.md`](docs/results.md) — the generated report (11 sections)
- [`docs/known-limitations.md`](docs/known-limitations.md) — what these numbers
  cannot support, including the absence of neural retrieval
- **Portfolio notes**
  - [What a retrieval score actually claims](docs/portfolio/01-what-a-retrieval-score-claims.md)
  - [Reachability is a ceiling, not an objective](docs/portfolio/02-reachability-is-a-ceiling.md)
  - [Pricing the decisions](docs/portfolio/03-pricing-the-decisions.md)
  - [Bugs the experiment found](docs/portfolio/04-bugs-the-experiment-found.md) — six defects, none of which threw, four of which agreed with the hypothesis
- **Decisions**
  - [0001 Generated corpus, not collected](docs/adr/0001-generated-corpus.md)
  - [0002 Binary relevance plus a harm metric](docs/adr/0002-binary-relevance-plus-harm.md)
  - [0003 A minimal plural stripper, not Porter](docs/adr/0003-no-porter-stemmer.md)
  - [0004 Counted operations, not wall-clock](docs/adr/0004-counted-ops-not-wall-clock.md)
  - [0005 Paired t over permutation](docs/adr/0005-paired-t-over-permutation.md)

## Honest scope

The corpus is synthetic, so **absolute scores here mean nothing** — only
differences measured on identical material do. There is no neural retrieval:
LSA is the distributional stand-in and the "cross-encoder-style" reranker is a
linear model over lexical features. Cost is counted operations, so §9's
frontier is a frontier in algorithmic work, not a latency curve. The lab stops
at retrieval; no generator is run, so `misleading@1` is a well-founded proxy
rather than a measurement of wrong answers.

The four structural findings don't depend on any of that:  reachability bounds
every retriever equally, document-level labels decorrelate for every retriever
equally, the minimum detectable effect is a property of the query set, and
`1/(B+1) < alpha/m` is arithmetic. See
[`docs/known-limitations.md`](docs/known-limitations.md) for the full list.
