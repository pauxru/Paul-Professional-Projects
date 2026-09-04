# ADR 0002: Binary relevance plus a separate harm metric, not graded relevance

## Status

Accepted.

## Context

The corpus contains a specific and common failure: a chunk that holds the
*right value* under the *wrong qualifier*, or under no qualifier at all.
"Retained for 400 days before automatic deletion" is the correct answer for one
plan and a confidently wrong answer for the other two.

Graded relevance is the standard tool for "partly right". TREC-style
judgements run 0–3, and nDCG is designed to consume them. The obvious move is
to score such a chunk 1 instead of 3.

That move is wrong, and it is wrong in a way that matters more in a RAG
pipeline than in a search results page.

## Decision

Relevance is binary. A chunk either contains everything needed to answer, or it
does not.

Harm is counted separately, by two metrics that no leaderboard reports:

- `misleading@k` — the top k contains a chunk holding a target's value without
  its qualifier, or a sibling fact's value. Material a generator will render
  into a fluent, cited, wrong answer.
- `misleading@1` — the same, at rank 1, where a generator is most likely to
  ground on it.
- `unanswered@k` — nothing relevant *and* nothing misleading was returned.

## Rationale

A partially relevant document in a search results page is genuinely partially
useful: the human reading it can see it is about the wrong plan and keep
scrolling. A chunk in a RAG context window has no such reader. It is
concatenated into a prompt and rendered into prose by a model with no way to
know that the heading it needed was two hundred tokens away in a chunk that was
not retrieved.

So the utility of a chunk holding the right value under the wrong qualifier is
not 0.3 of the utility of a correct one. It is *negative*. Giving it a positive
graded gain does not merely mis-scale the metric, it gets the sign wrong, and
it does so in the direction that makes a dangerous system look mediocre rather
than broken.

The separation also makes the two failure modes visible as distinct events.
`unanswered@k` is the *good* failure: the system returns visibly off-topic
material, a grounded generator declines, and a human notices. `misleading@k` is
the bad one: the system returns plausible material and the generator answers
confidently. Aggregating both into "not relevant" destroys the distinction, and
the distinction is the whole of the operational risk.

## Consequences

Section 8 of the report is the payoff. Two configurations that differ by 0.15
nDCG differ by a factor of five on `misleading@1` in the *opposite* direction
from what nDCG suggests, and the chunker that repeats its heading path into
every chunk removes an entire failure mode at no measurable cost. On nDCG
alone that decision is invisible.

The cost is that the report cannot be compared to published nDCG figures. It
already could not — the corpus is different — but this makes it explicit.

A second consequence is that `judge()` needs a rule for a chunk that is both:
a table row states every plan's value beside the label that distinguishes them,
so the same chunk covers a target and a sibling. It answers the question, so it
is relevant and is *not* counted as harm. `harmful -= relevant` in
`metrics.judge`, pinned by
`test_judge_never_calls_a_relevant_chunk_misleading`.

## Alternatives considered

**Graded relevance 0–3.** Rejected above.

**Negative gains in nDCG.** Considered seriously. Rejected because nDCG's
normaliser assumes non-negative gains; with negative gains the ideal is no
longer an upper bound and the metric stops being interpretable as a fraction.
Counting harm in its own metric keeps both quantities meaningful.

**A single combined "safety-adjusted nDCG".** Rejected. It requires choosing an
exchange rate between a missed answer and a wrong answer, that rate is entirely
application-specific, and burying it inside a metric hides the one number a
reader most needs to disagree with. The report shows both columns and lets the
reader price them.
