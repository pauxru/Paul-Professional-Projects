# Pricing the decisions

Sections 5, 7, 8 and 9 of the report exist to answer questions somebody
actually has to decide: is a reranker worth building, is tuning on the eval set
a real risk, which configuration ships, and what does the last nDCG point cost.
Three of the four answers are "no" or "less than you think", and the reasoning
is more useful than the answers.

## Is a reranker worth building?

The ceiling argument is easy and correct: a reranker reorders a candidate pool,
it cannot add to it, so its ceiling is pool recall — which section 1 already
measured.

The prediction that follows looks equally easy: rerank gain should be near zero
where pool recall is low (nothing to promote) and larger where pool recall is
high (room to reorder). Positive relationship.

It is negative. **Correlation −0.564.** Configurations in the bottom half by
pool recall gain +0.068 on average; the top half gain +0.024.

The reason is visible once stated: where pool recall is high, the pool ordering
is *also* already good, so there is nothing left for a reranker to fix. Where
pool recall is low, the pools are badly ordered and there is signal left in
them. `sentence_window_1/tfidf` gains +0.139 from reranking — the largest gain
in the grid — and still lands at nDCG 0.300, well below the *unreranked*
leader at 0.704.

The practical conclusion survives either sign, and it is the one worth
carrying:

> Rerank gains measured on one pipeline do not transfer to another, because the
> gain is a property of how badly the pool was ordered, not of the reranker.

A vendor benchmark showing "+15% from our reranker" is a statement about the
first stage they measured against. If your first stage is better ordered, you
will get less. If it is worse, you may get more — and you would get more from
fixing the first stage.

The best configuration in the grid gains **+0.002** from its best reranker.

## Is tuning on the evaluation set actually a problem?

The `fitted` reranker learns seven feature weights by coordinate ascent on
nDCG. The honest protocol fits on half the queries and reports on the other
half. To price the shortcut everyone actually takes, the same fit is also run
*on the reporting half itself*, and both numbers appear side by side.

Mean optimism across 35 configurations: **+0.016 nDCG.** Worst case: **+0.091**
(`sentence_window_1/rrf`).

Against a minimum detectable effect of 0.046, mean optimism of 0.016 is below
the noise floor. On this corpus, with this seven-parameter model, tuning on the
eval set would not by itself fabricate a result.

That is a narrower claim than it looks and the report says so explicitly. Seven
weights on 273 queries is a low-capacity model. The number scales with
capacity, and the moment the "reranker" is a neural model with millions of
parameters, or the moment the tuning includes chunk size and retriever choice
and top-k — a search over the whole grid rather than seven weights — the
optimism is a different quantity entirely. What transfers is the *protocol*:
report both columns, and let the gap be visible.

The worst case is instructive too. The largest optimism, +0.091, occurs on the
weakest configurations. A model that has little real signal to fit will fit
noise instead, which is exactly where the shortcut is most dangerous and where
a practitioner is most likely to be reaching for it.

## Which configuration ships?

The leading configuration is `recursive_240/bm25/none` at nDCG@10 **0.704**.

Two other configurations are statistically indistinguishable from it after a
paired, Holm-corrected analysis over 595 comparisons. Twelve sit within the
minimum detectable effect. Choosing among those on score is choosing on noise.

So the report refuses to pick and instead names the axes worth deciding on:
cost, `misleading@1`, and operational preference. That is not evasion; it is
the only defensible reading of a table where the top of the ranking is inside
the resolution of the instrument.

Note also what wins: a plain recursive splitter at 240 tokens with BM25 and no
reranker. The most elaborate thing in the grid is not on the frontier.

## What does the last nDCG point cost?

Cost is counted, not timed — multiply-accumulate operations charged per query
(ADR 0004). Wall-clock on a shared machine measures the machine, and a cost
axis that moves between runs cannot appear on a frontier that is supposed to be
reproducible.

**2 of 140 configurations are on the Pareto frontier. 98.6% of the grid is
strictly dominated** — worse *and* more expensive than something else in the
same table.

| query ops | nDCG@10 | configuration | index bytes |
|---|---|---|---|
| 138 | 0.694 | fixed_240/bm25/none | 476,448 |
| 143 | 0.705 | recursive_240/bm25/fitted | 493,760 |

The whole frontier is two points 0.011 nDCG apart. 0.011 is a quarter of the
minimum detectable effect, so the frontier's two points are not distinguishable
either. The real finding is the 98.6%: nearly everything anyone might reach for
is dominated, and the dominating options are the cheap, unfashionable ones.

## The cost nobody prices: harm

nDCG, MRR and recall all answer "did the right thing appear?" None answers "did
a *convincing wrong thing* appear instead?"

That distinction is the operational risk in a RAG system. A chunk holding the
right value under the wrong plan is not 30% useful. In a search results page a
human sees the wrong plan and scrolls on. In a context window there is no such
reader — the chunk is concatenated into a prompt and rendered into confident
prose by a model with no way to know the heading it needed was in a chunk that
was not retrieved.

Two counters make it visible:

- `misleading@1` — the top result holds a target's value without its qualifier,
  or a sibling's value. Material a generator will cite and get wrong.
- `unanswered@10` — nothing relevant *and* nothing misleading. The **good**
  failure: the system visibly has nothing, a grounded generator declines, a
  human notices.

Aggregating both into "not relevant", as every standard metric does, destroys
the distinction — and the distinction is the whole of the risk.

| chunker | nDCG@10 | misleading@1 | unanswered@10 |
|---|---|---|---|
| fixed_120 | 0.567 | **0.055** | 0.216 |
| structural_240 | 0.399 | 0.037 | 0.238 |
| structural_prefixed_240 | 0.532 | **0.033** | 0.289 |
| recursive_240 | 0.704 | 0.007 | 0.106 |

The worst configuration puts a misleading chunk first on **5.5%** of queries —
one query in eighteen produces a confident, cited, wrong answer.

And the pair that matters: `structural_240` and `structural_prefixed_240`
differ by 0.133 nDCG, which reads as "one is much worse". They differ on
`misleading@1` by 0.004, which reads as "barely anything". The second reading
is the wrong one. Repeating the heading path makes a value-without-qualifier
chunk *structurally impossible* — the residual 0.033 is sibling confusion from
table rows, a different mechanism. nDCG cannot express "removed a failure
mode"; it can only express "scored a bit higher".

If you price a wrong answer at more than a missing answer — which any regulated
domain does — the harm column is the one you buy on, and it is not in any
leaderboard.

## The six-line version

- Measure the chunker before tuning the retriever; reachability bounds
  everything and needs no retrieval to compute.
- Do not chase reachability. The chunker with perfect reachability is the worst
  configuration in the grid.
- Label at span level or do not claim to compare chunkers.
- Report the minimum detectable effect first. Most of this grid is inside it.
- Count harm separately from relevance.
- Repeat the heading path into every chunk. Free, invisible to nDCG, removes an
  entire failure mode.
