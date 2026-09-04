# ADR 0002 — A top-K veto outside the similarity function, not a better threshold

**Status:** accepted

## Context

Section 1 of the report establishes something stronger than "the threshold is
hard to tune". For L2-normalised pooled vectors:

```
cos(a, b) = Σ_{f ∈ shared} sqrt(w_a(f) · w_b(f))
```

so the similarity *drop* caused by changing a set of features is exactly the
mass those features carried. Verified on 35 designed pairs, mean absolute error
0.000.

The consequence is arithmetic. In a twelve-token query, one swapped word carries
roughly a twelfth of the mass — a bit more once the bigrams containing it are
counted, which is why the first formulation of this bound was wrong on 32 of 35
pairs. Either way, "shipping to Germany" and "shipping to France" sit around
0.85, while genuine paraphrases like "how long does delivery take" and "when
will my parcel arrive" sit *lower*, because they share almost no surface form.

**The populations are interleaved, and the ordering is backwards.** A threshold
that rejects the top decile of confusable pairs discards 79.1% of genuine
paraphrases. This is not a tuning problem. A threshold is a cut on a scalar; if
the scalar orders the populations wrongly, no cut works.

## Decision

Stop asking the similarity function to carry information it structurally cannot.
Extract each query's **decisive tokens** and veto a hit when they disagree,
regardless of cosine.

Two questions follow: what is "decisive", and how are two sets compared.

### Rejected: an IDF threshold

The obvious definition of decisive is *high IDF*. It does not work here, and the
failure is instructive enough that the report keeps it.

Over 170 documents, 56.5% of the vocabulary occurs exactly once and 74.3% within
two documents. The IDF distribution is a spike. **Every percentile from the 10th
to the 90th returns the same cut value.** There is no threshold to tune: any
setting admits nearly every content word — `but`, `cannot`, `answer` alongside
`germany` and `401`.

Worse, the resulting decisive set grows with sentence length. That reintroduces
exactly the dilution the guard was built to escape: a long query has a large
decisive set, a large set is easier to match, and the veto stops firing on
precisely the queries where pooling has already failed.

### Accepted: a top-K budget

Take the **K highest-IDF tokens** of each query, however common they are.

K is a *budget*, not a threshold. It is insensitive to the shape of the IDF
distribution and to sentence length — the two things that broke the cut. Two
queries about different countries cannot agree on their top-K, because the
country name is the highest-IDF token in both. Two phrasings of the same
question usually can.

Sets are compared by Jaccard against a minimum. The sweep's best setting is K=3,
J=0.67 — take the top three tokens, require two of three to agree.

The veto sits **outside** the similarity function. It is not a term added to a
weighted average; it is a hard rejection. That is the entire mechanism: an
averaged score cannot let one feature overrule a document, so the feature that
must overrule has to leave the average.

## Results

| policy | hit rate | error (confusable) | precision | correct answers from cache |
|--------|----------|--------------------|-----------|----------------------------|
| no guard, threshold 0.60 | 90.6% | 3.7% | 96.7% | 3503 |
| K=3, J=0.67 | 89.6% | 0.0% | 100.0% | 3585 |

The sweep is scored on **correct answers served from cache**, not on hit rate or
precision. Hit rate is maximised by admitting everything; precision is maximised
by admitting nothing. The product is maximised by being right, and it cannot be
gamed by moving the threshold.

## The consequence that is easy to miss

The one-point hit-rate cost reads as almost free. It is not, and the report's
section 3b exists because a unit test said so.

Measured on the population the veto actually adjudicates — one stored phrasing,
a different phrasing of the same question — **it rejects 81.4% of them**
(36.8% served without the guard, 6.8% with it).

Both numbers are true. The aggregate barely moves because **81.8% of cache hits
in this workload are verbatim repeats**, whose decisive sets are identical by
construction and which the veto passes for free. The guard is nearly invisible
in aggregate precisely because it only touches the minority of traffic that the
word *semantic* refers to.

So the recommendation is narrower than the sweep implies:

- if the value case is deduplicating **repeated identical questions**, the veto
  is close to free and should be on;
- if the value case is consolidating **paraphrases** — the reason anyone builds
  a semantic cache rather than a hash map — the veto removes roughly four in
  five of the hits being paid for, and no aggregate dashboard will show it.

## Consequences

- The guard needs a per-entry decisive set, so `cache.Entry` carries `Rare` and
  `Put` recomputes it. The gateway's coalescing guard calls
  `Cache.DecisiveTokens` rather than reimplementing it — two definitions of
  "decisive" that drifted apart would make the report's comparison meaningless.
- `Stats.GuardVetoes` counts only vetoes that *changed an outcome*. If a correct
  entry is also above threshold it wins and no veto is recorded.
- J does not mean the same thing at different K, because the union grows with K.
  At K=1 it is entirely inert. This is why the sweep is non-monotone in K at
  fixed J, and it is reported as measured rather than reparameterised.
- The veto is a second signal, not a better model. Nothing here required
  retraining, re-embedding or a different provider — which is the point.
