# Retrieval Quality Lab: where the answer went

"Our RAG isn't working" is not one problem. It is at least three, they are
separable, and the separation is computable in advance. This report separates
them on a corpus whose relevance labels are exact by construction, and then
examines whether the differences it finds are real.

Grid: 7 chunkers x 5 retrievers x 4 rerankers = 140 configurations, each run
on the same 273 queries, cut at k=10 from a candidate pool of 50. Every
configuration is deterministic and this document is byte-reproducible.


## 0. The corpus, and why it is generated rather than collected

Every retrieval benchmark inherits the same defect from the way it was built:
an assessor read the documents and marked which ones answer each query.
Passages the assessor missed are not merely unlabelled, they are labelled
*irrelevant*, and every metric computed on top of them is biased by an amount
nobody can measure.

This corpus is built in the opposite direction. Facts are declared first, as
structured data. Documents are rendered from them in six layouts. The set of
passages that answers a query is therefore known exactly, and -- more
importantly -- so is the set that does not.

| quantity                   | value  |
|----------------------------|--------|
| fact families              | 30     |
| facts (family x qualifier) | 78     |
| documents holding answers  | 17     |
| near-miss documents        | 58     |
| analysed tokens            | 41,100 |
| labelled placements        | 160    |
| queries                    | 273    |
|   lexical                  | 78     |
|   paraphrase               | 78     |
|   implicit                 | 72     |
|   unqualified              | 30     |
|   multi                    | 15     |

The structure that makes the experiment work is that a value is rendered
*without* the qualifier that scopes it. The sentence "Retained for 400 days
before automatic deletion." answers nothing on its own; it answers a question
only in combination with the heading that says which plan it applies to. A
chunk holding the sentence and not the heading has kept the answer and thrown
away its meaning, and the labels can tell the difference.

Three invariants are enforced in code and by tests, because each of them, if
violated, would silently invalidate every number below:

- no near-miss document may contain a governed value, or it would be an
  unlabelled correct answer;
- no query may quote the value it asks for, or it becomes retrievable by exact
  match and measures nothing;
- every governed value must be a distinctive string, so the first check is
  meaningful. The fact table originally contained the value "4", which makes
  substring search useless and would have let a generated sentence reading
  "the default fan-out is 4" act as an unlabelled answer.


## 1. The cascade of ceilings

A pipeline can lose an answer in three places, and for a given (query, target)
pair exactly one of them is responsible. Testing in pipeline order makes the
assignment exclusive and exhaustive:

- **chunker** -- no chunk anywhere in the index covers the target;
- **retriever** -- a covering chunk exists but none reached the candidate pool
  (depth 50);
- **ranker** -- a covering chunk reached the pool but not the top 10;
- **served** -- a covering chunk is in the top k.

The four sum to one by construction, so the decomposition has no residual to
argue about. A defect in it does not produce a plausible split, it produces a
row that does not sum to one, and a test asserts that none does.

**Predicted.** Chunkers that cut small will have the highest reachability -- a smaller unit
is more likely to fall entirely inside one chunk -- and the reachability
ranking will therefore be roughly the inverse of chunk size. Reachability is
an upper bound and not an outcome, so it should not predict the final score.

| chunker                 | chunks | best retriever | reach | pooled | served | lost:chunker | lost:retriever | lost:ranker | binding   |
|-------------------------|--------|----------------|-------|--------|--------|--------------|----------------|-------------|-----------|
| overlap_240_120         | 467    | bm25           | 1.000 | 0.932  | 0.896  | 0.000        | 0.068          | 0.036       | retriever |
| recursive_240           | 318    | bm25           | 1.000 | 0.935  | 0.908  | 0.000        | 0.065          | 0.027       | retriever |
| structural_240          | 601    | bm25           | 1.000 | 0.845  | 0.723  | 0.000        | 0.155          | 0.122       | retriever |
| structural_prefixed_240 | 601    | bm25           | 1.000 | 0.905  | 0.744  | 0.000        | 0.095          | 0.161       | ranker    |
| sentence_window_1       | 5245   | bm25           | 1.000 | 0.702  | 0.417  | 0.000        | 0.298          | 0.286       | retriever |
| fixed_240               | 287    | bm25           | 0.988 | 0.923  | 0.905  | 0.012        | 0.065          | 0.018       | retriever |
| fixed_120               | 541    | bm25           | 0.946 | 0.860  | 0.759  | 0.054        | 0.086          | 0.101       | ranker    |

**Found — prediction wrong.** The prediction is wrong, and it is wrong in the one place where only size
varies. `fixed_120` cuts half as long as `fixed_240` and reaches 0.946 against
0.988 -- smaller units are *worse*. The reason is that a target is a
conjunction. Halving the window makes each individual span more likely to fit
and the pair less likely to co-occur, and it is the pair that carries the
meaning. `sentence_window_1` reaches 1.000 not because its unit is small but
because the window re-attaches the neighbouring sentence, which is a different
mechanism from cutting small.

The second half of the prediction holds, and the consequence is the opposite
of the intuition that motivates small chunks: `sentence_window_1` reaches
1.000 of targets and serves 0.417, the worst in the grid. It did not remove
the loss, it moved it: chunker loss 0.000 against retriever loss 0.298.
Splitting into 5,245 units gives every answer a home and gives the retriever
16x more places to look, each too short to carry enough signal to be found.

This is the shape of the whole problem. The stage you measure is the stage you
fix, and fixing it in isolation relocates the failure to a stage you were not
measuring, where it is invisible. The binding column names the stage that is
actually costing the most for each configuration; for five of seven chunkers
it is the retriever, which is not where the chunking literature suggests
looking.


### 1a. Reachability saturates; severance does not

The reachability column above is computed per *fact*, and a fact appears in up
to three layouts. A chunker can destroy an answer in one layout and still
score 1.000 because redundancy rescued it elsewhere. That is a property of the
corpus, not of the chunker, and it is why five of seven rows tie at the top.
Measuring per *placement* -- each individual rendering of a fact in a document
-- removes the rescue and separates the two ways a chunking can fail:

- **lost** -- no chunk contains the sentence stating the value at all;
- **severed** -- some chunk contains the sentence, but no chunk contains it
  together with the heading that says which plan or region it applies to.

**Predicted.** The two failures call for opposite fixes -- lost answers want smaller units,
severed answers want larger ones -- so a useful chunker comparison has to
report them separately. Loss should dominate at small chunk sizes and
severance at large ones.

| chunker                 | chunks | placements intact | severed | lost | plan_matrix intact |
|-------------------------|--------|-------------------|---------|------|--------------------|
| fixed_120               | 541    | 114/160           | 39      | 7    | 9/39               |
| fixed_240               | 287    | 141/160           | 18      | 1    | 21/39              |
| overlap_240_120         | 467    | 142/160           | 18      | 0    | 21/39              |
| recursive_240           | 318    | 142/160           | 18      | 0    | 21/39              |
| structural_240          | 601    | 115/160           | 45      | 0    | 0/39               |
| structural_prefixed_240 | 601    | 160/160           | 0       | 0    | 39/39              |
| sentence_window_1       | 5245   | 103/160           | 57      | 0    | 0/39               |

**Found — prediction wrong.** Severance is not one failure mode among two, it is essentially the only one.
Across all seven chunkers and 160 placements each, 195 placements were severed
and 8 were lost outright. The prediction that loss dominates at small sizes is
contradicted: `sentence_window_1`, the smallest unit in the grid, loses
nothing and severs 57, the most in the grid.

The controlled comparison is the last two structural rows. `structural_240`
and `structural_prefixed_240` produce the *identical* 601 chunk boundaries;
the only difference is that the second repeats the heading path into the body
of each chunk. Severance goes from 45 to 0, and `plan_matrix` -- the layout
where every value is scoped by a heading -- goes from 0/39 to 39/39. Both
chunkers report a fact-level reachability of 1.000.

> This is the single most useful number in the report and it costs one pass over
> the index with no retrieval, no queries and no model. If a chunking severs
> answers from the headings that scope them, every downstream measurement is
> bounded by that and no amount of retriever tuning will recover it.


## 2. Document-level labels cannot measure chunking

Public retrieval benchmarks label relevance at document level: an assessor
marks a document as answering a query, and every chunk of that document
inherits the label. Applying that judging rule to the identical rankings
isolates one variable -- label granularity -- and prices it.

**Predicted.** Under document-level labels a chunk is relevant whenever it comes from the
right document, so a chunker that severs an answer from its qualifier is
scored as though it had not. The spread between the best and worst chunker
should collapse towards zero, and the ordering should change.

| chunker                 | nDCG@10 (span labels) | rank | nDCG@10 (doc labels) | rank |
|-------------------------|-----------------------|------|----------------------|------|
| recursive_240           | 0.704                 | 1    | 0.599                | 2    |
| fixed_240               | 0.694                 | 2    | 0.595                | 3    |
| overlap_240_120         | 0.692                 | 3    | 0.616                | 1    |
| fixed_120               | 0.567                 | 4    | 0.471                | 6    |
| structural_prefixed_240 | 0.532                 | 5    | 0.537                | 5    |
| structural_240          | 0.399                 | 6    | 0.423                | 7    |
| sentence_window_1       | 0.204                 | 7    | 0.567                | 4    |

**Found.** Span-level labels separate the chunkers by 0.499 nDCG. Document-level labels
separate them by 0.194, and 6 of 7 chunkers change rank. The direction is as
predicted; the size is the part worth carrying away. An evaluation with
document-level labels is not a weak instrument for comparing chunkers -- it is
not an instrument for comparing chunkers, and it will report a confident
ordering anyway.

> Both columns are computed from the same rankings produced by the same runs.
> Nothing about the systems differs between them. The only difference is what
> the judge was allowed to see.

One more property of the doc-level column is worth stating, because the
obvious reading of it is wrong. Document-level labels are a superset of
span-level ones, so the natural expectation is that they inflate every score.
They do not, because nDCG normalises by the ideal and the ideal grows with the
relevant set: a ranking that filled every slot it could under span labels no
longer fills every slot the larger ideal assumes.

**Predicted.** If the effect were pure inflation, per-query scores would move in one
direction only. If instead the growing normaliser matters, they will move in
both, and the correlation between the two judging rules will be well below
one.

| queries scored higher | scored lower | unchanged | correlation |
|-----------------------|--------------|-----------|-------------|
| 32                    | 158          | 83        | 0.885       |

**Found.** On the leading configuration 32 queries score higher under document-level
labels, 158 score lower and 83 are unchanged; the two judging rules correlate
at 0.885. Document-level labels do not inflate the measurement, they
decorrelate it. That is the stronger objection: a biased instrument can be
corrected for, a decorrelated one cannot.


## 3. Most of the grid is noise

The 35 chunker x retriever combinations yield 595 pairwise comparisons. Every
configuration ran on the identical 273 queries, so the unit of analysis is the
per-query difference and the correct test is paired.

**Predicted.** Two effects push in opposite directions and both are large. Pairing removes
between-query variance, which in retrieval dwarfs between-system variance, so
the paired test will find far more real differences than the unpaired one.
Correcting for 595 simultaneous comparisons will remove many of them again.
The naive analysis -- unpaired and uncorrected -- will report the most
significant results of all four combinations, and its extra findings are
false.

| analysis                 | significant at 0.05 | share of comparisons |
|--------------------------|---------------------|----------------------|
| unpaired, uncorrected    | 438                 | 73.6%                |
| unpaired, Holm-corrected | 380                 | 63.9%                |
| paired, uncorrected      | 537                 | 90.3%                |
| paired, Holm-corrected   | 491                 | 82.5%                |

**Found — prediction wrong.** The paired, corrected analysis -- the only defensible one -- finds 491 real
differences out of 595, 82.5%. The naive analysis finds 438. The prediction
was wrong in direction: pairing gains more than correction loses, so the
defensible analysis finds 53 *more* differences than the naive one. Discarding
the pairing does not merely inflate significance, it destroys the power to see
the effects that are actually there -- and the two errors do not cancel,
because they act on different comparisons.

> The comparisons the two analyses disagree about are not interchangeable. Naive
> significance is concentrated in pairs with large mean differences; paired
> significance is concentrated in pairs with small, consistent ones. A method
> that only sees the former will systematically miss the improvements that are
> worth shipping -- small and reliable -- and systematically endorse the ones
> that are not.


### 3a. The correction the resampling budget cannot satisfy

The paired row above is computed with a t-test, and the reason is a defect
this report shipped in an earlier revision. The permutation test is the better
instrument -- it assumes nothing about the distribution of per-query
differences, which are mostly exactly zero with a heavy tail -- but it is a
*counting* procedure, and it cannot report a p-value below 1/(B+1). At B =
4,000 that floor is 2.50e-04. Holm's strictest threshold over 595 comparisons
is 0.05/595 = 8.40e-05.

**Predicted.** The floor is above the threshold, so no comparison in this family can clear it
regardless of how large its effect is. The permutation column will report
exactly zero significant differences, and it will look like a finding about
retrieval rather than a fact about the resampling budget.

| test                           | raw p < 0.05 | Holm-corrected p < 0.05 | smallest raw p |
|--------------------------------|--------------|-------------------------|----------------|
| paired permutation (B = 4,000) | 537          | 0                       | 2.50e-04       |
| paired t                       | 536          | 491                     | 3.56e-78       |

**Found.** The permutation test finds 537 raw differences and 0 after correction; its
smallest attainable p-value is 2.50e-04, which is the floor itself. The
t-test, on the identical differences, finds 491 after correction with a
smallest raw p-value of 3.56e-78. The zero was an artefact of arithmetic, not
a property of the systems. Recovering it with a permutation test would need B
> 11,900 -- 3.0x the current budget on every one of 595 comparisons.

> The general rule: a randomisation test and a family-wise correction constrain
> each other. Before running one inside the other, check that 1/(B+1) < alpha/m.
> If it is not, the table will fill with zeros and read as a result. The defect
> is invisible precisely because 'nothing was significant after correction' is
> exactly what an honest, well-powered-but-null experiment also looks like.


## 4. What this corpus cannot measure

Before asking which configuration wins, ask what size of difference this
corpus is capable of resolving at all. For a paired test the answer follows
from the standard deviation of the per-query differences and the number of
queries.

**Predicted.** Per-query nDCG differences are mostly exactly zero with a heavy tail, so their
standard deviation will be a substantial fraction of the nDCG scale. With a
few hundred queries the smallest detectable difference will be on the order of
a few nDCG points -- comparable to the differences typically reported as
improvements.

| queries           | minimum detectable nDCG@10 difference |
|-------------------|---------------------------------------|
| 50                | 0.107                                 |
| 100               | 0.076                                 |
| 273 (this corpus) | 0.046                                 |
| 500               | 0.034                                 |
| 1000              | 0.024                                 |
| 5000              | 0.011                                 |

**Found.** The median standard deviation of paired differences is 0.270. With 273 queries
the minimum detectable difference at 80% power is 0.046 nDCG. To resolve a
one-point difference would take 5,726 queries; two points, 1,432. 12 of the 35
configurations sit within 0.046 of the leader, which means this corpus cannot
order them, and neither can any report built on it -- including this one.

> This is the number that should appear first in every retrieval comparison and
> appears in almost none. A fifty-query evaluation -- common in practice --
> cannot resolve anything smaller than 0.107 nDCG, which is larger than the
> difference between most of the configurations anyone argues about.


## 5. What tuning on the evaluation set is worth

The `fitted` reranker learns seven feature weights by coordinate ascent on
nDCG. It is fitted on half the queries and reported on the other half. To
price the shortcut that is taken instead, the same fitting is also run *on the
reporting half itself*, and both numbers are shown.

**Predicted.** Seven weights on a few hundred queries is not a high-capacity model, so the
optimism should be visible but modest -- larger than the differences between
neighbouring configurations, smaller than the spread of the grid.

| chunker/retriever             | held out (honest) | fitted on the reported half | optimism |
|-------------------------------|-------------------|-----------------------------|----------|
| sentence_window_1/rrf         | 0.227             | 0.318                       | +0.091   |
| sentence_window_1/lsa         | 0.256             | 0.318                       | +0.062   |
| sentence_window_1/blend       | 0.229             | 0.288                       | +0.060   |
| structural_prefixed_240/tfidf | 0.512             | 0.557                       | +0.045   |
| structural_prefixed_240/lsa   | 0.517             | 0.555                       | +0.039   |
| sentence_window_1/bm25        | 0.281             | 0.311                       | +0.029   |
| fixed_120/tfidf               | 0.555             | 0.581                       | +0.025   |
| structural_prefixed_240/rrf   | 0.541             | 0.559                       | +0.018   |

**Found.** Mean optimism across the 35 chunker x retriever combinations is +0.016 nDCG;
the worst is +0.091. Against a minimum detectable effect of 0.046, an optimism
of 0.016 is smaller than the noise floor, so on this corpus the shortcut would
not by itself fabricate a result -- which is a statement about this corpus and
this seven-parameter model, not about the practice.

The seven features and the weights the honest fit selected, for the leading
configuration:

| feature      | hand-chosen weight | fitted weight (recursive_240/bm25) |
|--------------|--------------------|------------------------------------|
| coverage     | 1.00               | 3.00                               |
| idf_coverage | 1.50               | 1.00                               |
| proximity    | 0.80               | 1.00                               |
| bigram       | 0.60               | 3.00                               |
| has_heading  | 0.30               | 0.25                               |
| brevity      | 0.20               | -0.50                              |
| pool_rank    | 0.50               | 3.00                               |


## 6. The aggregate winner loses on the class that matters

Queries fall into five classes that fail differently. `implicit` is the
interesting one: the qualifier is never named, only implied ("for our largest
accounts" rather than "Enterprise"), so no term weighting can reach it -- the
query and the heading share no token.

**Predicted.** The lexical retrievers will win overall, because most queries share vocabulary
with the documents. On `implicit` they should collapse and the distributional
retriever should win, since that class is exactly the case term matching
cannot serve.

| class       | queries | aggregate winner (recursive_240/bm25/none) | best for this class       | its score | gain   |
|-------------|---------|--------------------------------------------|---------------------------|-----------|--------|
| lexical     | 78      | 0.991                                      | overlap_240_120/bm25/none | 0.993     | +0.002 |
| paraphrase  | 78      | 0.589                                      | fixed_240/bm25/none       | 0.592     | +0.003 |
| implicit    | 72      | 0.388                                      | fixed_240/bm25/none       | 0.393     | +0.005 |
| unqualified | 30      | 0.953                                      | overlap_240_120/bm25/none | 0.956     | +0.003 |
| multi       | 15      | 0.832                                      | recursive_240/tfidf/none  | 0.855     | +0.024 |

**Found — prediction wrong.** An oracle that routed each class to its best configuration would score 0.708
against 0.704 for the best single configuration, a gain of +0.005 using 3
distinct configurations. On `implicit` specifically, BM25 scores 0.388 and the
distributional index scores 0.330 on the identical chunking -- the predicted
inversion does not appear. Latent semantic indexing over this corpus does not
learn that "largest accounts" means Enterprise, because the two never
co-occur: the corpus was written by a generator that had no reason to put them
in the same chunk. The class remains unserved by everything in the grid, which
is a more useful finding than a win would have been.

Whether +0.005 justifies building a router is a cost question, and the honest
answer here is no: the gain is comparable to the minimum detectable effect
from section 4. The value of the table is not the routing gain, it is that the
aggregate hides a class the whole grid fails at.


## 7. Reranking amplifies retrieval, it does not repair it

A reranker reorders the candidate pool. It cannot add to it. Its ceiling is
therefore the fraction of targets whose covering chunk reached the pool, and
that quantity is already in the ladder from section 1.

**Predicted.** Rerank gain should be near zero where pool recall is low -- there is nothing
to promote -- and larger where pool recall is high but the ordering within the
pool is poor. Plotted against pool recall the relationship should be positive.

| chunker/retriever       | pool recall (depth 50) | nDCG@10 unranked | best reranker | gain   |
|-------------------------|------------------------|------------------|---------------|--------|
| sentence_window_1/lsa   | 0.664                  | 0.174            | fitted        | +0.092 |
| sentence_window_1/blend | 0.685                  | 0.184            | fitted        | +0.068 |
| sentence_window_1/rrf   | 0.688                  | 0.194            | fitted        | +0.055 |
| sentence_window_1/tfidf | 0.690                  | 0.161            | fitted        | +0.139 |
| recursive_240/tfidf     | 0.929                  | 0.671            | fitted        | +0.017 |
| overlap_240_120/bm25    | 0.932                  | 0.692            | mmr           | +0.005 |
| recursive_240/rrf       | 0.932                  | 0.680            | fitted        | +0.012 |
| recursive_240/bm25      | 0.935                  | 0.704            | fitted        | +0.002 |

**Found — prediction wrong.** Across all 35 chunker x retriever combinations the correlation between pool
recall and the best available rerank gain is -0.564. Configurations in the
lower half by pool recall gain +0.068 on average; the upper half gain +0.024.
The relationship is negative, contradicting the prediction. The reason is
visible in the ladder: where pool recall is already high the pool ordering is
also already good, so there is little for a reranker to fix, while the
low-recall configurations have badly ordered pools with some signal left in
them. The ceiling argument is still correct -- a reranker cannot exceed pool
recall -- but it does not imply that high pool recall leaves room to gain.

> Either way the practical conclusion is the same and it is the one worth
> taking: rerank gains measured on one pipeline do not transfer to another,
> because the gain is a property of how badly the pool was ordered, not of the
> reranker.


## 8. The failure nobody counts

nDCG, MRR and recall all answer "did the right thing appear?". None answers
"did the wrong thing appear instead?". In this corpus the distinction is
sharp, because a chunk can hold the correct value under the wrong plan, or the
correct value under no plan at all. Both produce a fluent, cited, wrong
answer.

Two counters make it visible. `misleading@1` is the rate at which the top
result is such a chunk. `unanswered@10` is the rate at which nothing relevant
*and* nothing misleading is returned -- the good failure, where the system
visibly has nothing and a grounded generator declines.

**Predicted.** Chunkers that separate a value from its qualifier will produce more misleading
top results. `structural_prefixed_240` repeats the heading path into every
chunk, so by construction it can never produce a chunk holding a value without
its qualifier, and its misleading rate should come only from sibling
confusion.

| chunker                 | retriever | nDCG@10 | misleading@1 | misleading@10 | unanswered@10 |
|-------------------------|-----------|---------|--------------|---------------|---------------|
| fixed_120               | bm25      | 0.567   | 0.055        | 0.101         | 0.216         |
| sentence_window_1       | bm25      | 0.204   | 0.048        | 0.132         | 0.432         |
| structural_240          | bm25      | 0.399   | 0.037        | 0.138         | 0.238         |
| structural_prefixed_240 | bm25      | 0.532   | 0.033        | 0.126         | 0.289         |
| overlap_240_120         | bm25      | 0.692   | 0.015        | 0.134         | 0.110         |
| fixed_240               | bm25      | 0.694   | 0.007        | 0.086         | 0.110         |
| recursive_240           | bm25      | 0.704   | 0.007        | 0.087         | 0.106         |

**Found.** The worst configuration puts a misleading chunk first on 5.5% of queries.
`structural_prefixed_240` reads 0.033 against 0.037 for the same strategy
without heading propagation -- repeating the heading path costs nothing and
removes an entire failure mode. Note that the two are not distinguishable on
nDCG (0.532 against 0.399), which is the point: the metric everyone reports
cannot see the difference between them.


## 9. The frontier, and what is dominated

Cost here is counted, not timed. Wall-clock on a shared machine measures the
machine, and a cost axis that moves between runs cannot appear on a frontier
that is supposed to be reproducible. The count is multiply-accumulate
operations charged by a query: postings visited for BM25, matrix rows times
dimensions for the dense indexes, and the sum of both for the hybrids.

**Predicted.** Dense retrieval over many small chunks will be the most expensive and among
the worst, so the frontier should be short: a handful of configurations, most
of the grid dominated.

| query ops | nDCG@10 | configuration             | index bytes |
|-----------|---------|---------------------------|-------------|
| 138       | 0.694   | fixed_240/bm25/none       | 476,448     |
| 143       | 0.705   | recursive_240/bm25/fitted | 493,760     |

**Found.** 2 of 140 configurations are on the frontier; 98.6% of the grid is strictly
dominated. The cheapest frontier point scores 0.694 at 138 ops; the best
scores 0.705 at 143 ops. Buying the last 0.011 nDCG costs 1.04x the query
work.


## 10. What to actually do

The leading configuration is `recursive_240/bm25/none` at nDCG@10 0.704. 2
other configurations are statistically indistinguishable from it on this
corpus. Choosing between those on score is choosing on noise; choose on cost,
on `misleading@1`, or on operational preference.

- **Measure the chunker before tuning the retriever.** The reachability column
  is computable from the chunking and the labels alone, with no retrieval, and
  it bounds everything downstream. If it is low, nothing else matters.
- **Do not chase reachability.** The chunker with perfect reachability is the
  worst configuration in the grid. Reachability is a ceiling, not an
  objective.
- **Label at span level or do not claim to compare chunkers.** Document-level
  labels reduced the chunker spread to 0.194 nDCG and reordered them.
- **Report the minimum detectable effect first.** Most of this grid is inside
  it.
- **Count harm separately.** Two configurations indistinguishable on nDCG
  differ by an entire failure mode on `misleading@1`.
- **Repeat the heading path into every chunk.** It is a few lines of code, it
  costs nothing measurable, and it eliminates chunks that hold a value without
  the qualifier that scopes it.


---

12 predictions were written before the corresponding measurement was read. 7 held; 5 did not, and each of those is discussed where it appears.
