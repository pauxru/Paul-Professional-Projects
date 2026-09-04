# What a retrieval score actually claims

A RAG evaluation reports a number — nDCG@10 = 0.71, say — and a comparison
table, and the reader is expected to conclude that the winning row is the
configuration to ship. This note is about the claims that number is quietly
making, most of which are not true of the way retrieval evaluations are
normally built.

## Claim 1: "the labels are complete"

nDCG is a fraction: achieved discounted gain over ideal discounted gain. The
ideal is computed from the labelled relevant set. If a passage answers the
query and nobody labelled it, two things happen at once — the ideal is too
small, and a system that returns that passage is charged for returning
something irrelevant.

Standard benchmarks are built by pooling: run a set of systems, take the top
results, have assessors judge those. Anything outside the pool is unjudged and,
in scoring, treated as non-relevant. The bias this creates is not random. It is
largest for systems *least* similar to the ones used to build the pool — which
is to say, largest for exactly the novel approach being evaluated.

You cannot measure this bias from inside the benchmark. There is no residual,
no diagnostic, no warning.

This lab avoids it by inverting the construction: facts are declared as
structured data first, documents are *rendered* from them, and each rendering
records the exact character span of the sentence stating the value plus the
spans of the headings that scope it. Relevance is then a set operation on
offsets, not a judgement. The negatives are as trustworthy as the positives —
which is the precondition for section 8's harm metrics existing at all.

The price is that the corpus is synthetic and absolute scores do not transfer.
That price is stated in the README, in ADR 0001, and again in section 0 of the
report. It is a smaller price than an unmeasurable bias.

## Claim 2: "these labels can compare chunkers"

They usually cannot, and this one is worth dwelling on because almost every
published chunking comparison makes the claim.

Assessors label *documents*. Chunkers produce *fragments* of documents. There
is no assessor judgement about whether a fragment answers a query, so the
universal practice is to inherit the document's label: any chunk from a
relevant document counts as relevant.

Under that rule a chunker that shatters a document into fragments too small to
answer anything is credited for every fragment. The judge cannot see the thing
the chunker is being judged on.

Section 2 measures it on identical rankings from identical runs — nothing about
the systems changes between the two columns, only what the judge is allowed to
see. Under span-level labels the seven chunkers spread across a wide range and
order one way. Under document-level labels the spread collapses to 0.194 nDCG
and the order changes.

The obvious explanation — document labels are a superset, so they inflate
everything — is wrong, and the report says so. nDCG normalises by the ideal,
and the ideal grows with the relevant set. Per-query scores move in *both*
directions: 32 queries score higher, 158 lower, and the correlation between the
two judging rules is 0.885. Document-level labels do not inflate. They
**decorrelate**. That is a stronger objection than inflation, because an
inflated metric still ranks systems correctly.

## Claim 3: "this difference is real"

Section 4 asks what size of difference the corpus can resolve at all, before
asking which configuration wins. The median standard deviation of per-query
paired differences is 0.270 nDCG. At 273 queries and 80% power the minimum
detectable difference is **0.046 nDCG**.

Twelve of the 35 configurations sit within 0.046 of the leader. This corpus
cannot order them, and neither can this report — which section 10 says in
those words.

For scale: a 50-query evaluation, which is common in practice and generous by
the standards of a blog post, cannot resolve anything below **0.107 nDCG**.
That is larger than the difference between most configurations anyone argues
about. Resolving a one-point nDCG difference on this corpus would take 5,726
queries.

This number is computable before any system is built. It should be the first
line of every retrieval comparison and it is in almost none of them.

## Claim 4: "the aggregate is the thing to optimise"

Section 6 splits 273 queries into five classes that fail differently. The
aggregate winner scores 0.704. On the `implicit` class — where the qualifier is
never named, only implied ("for our largest accounts" rather than
"Enterprise") — the entire grid tops out at 0.393.

An oracle routing each class to its best configuration gains +0.005. That is
smaller than the minimum detectable effect, so the honest conclusion is *do not
build the router*. The value of the class table is not the routing gain. It is
that one query class in five is failed by every configuration in the grid, and
the aggregate number cannot show you that.

## Claim 5: "the metric captures what goes wrong"

nDCG, MRR and recall all answer one question: did the right thing appear?

None of them answers: did a *convincing wrong thing* appear instead?

In this corpus that distinction is sharp, because a chunk can hold the correct
value under the wrong plan ("400 days" is right for one plan and wrong for two)
or the correct value under no plan at all. Either one becomes a fluent, cited,
wrong answer in a generated response, and the human reading it has no signal
that anything went wrong.

Section 8 counts it. The worst configuration puts such a chunk at rank 1 on
5.5% of queries. And the decisive comparison: `structural_240` and
`structural_prefixed_240` produce byte-identical chunk boundaries — the sole
difference is that the second repeats the heading path into each chunk body.
They differ by 0.133 nDCG, which reads as "the plain one is much worse". They
also differ on `misleading@1`, 0.037 against 0.033, in the same direction but
at a scale nDCG cannot express: the prefixed chunker *structurally cannot*
produce a chunk holding a value without its qualifier. It removes a failure
mode. nDCG has no way to say that.

## What a score would have to include to support the usual claim

1. The minimum detectable effect for the query set, stated first.
2. The label granularity, and whether it matches the unit being compared.
3. Whether negatives are trustworthy or merely unjudged.
4. A harm counter separate from the relevance counter.
5. Per-class results, because one class in five may be at zero.
6. The family size, if more than one comparison is being made — and, if a
   randomisation test is involved, proof that its resolution can satisfy the
   correction (see `04-the-correction-that-could-not-be-satisfied.md`).

Every one of those is cheap. The reason they are missing is not cost.
