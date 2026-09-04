# ADR 0001: Generate the corpus from structured facts instead of collecting one

## Status

Accepted.

## Context

The lab has to answer questions of the form "which stage of this pipeline lost
the answer". That requires knowing, for every query, exactly which passages
answer it — not approximately, and not only the ones somebody happened to
find.

Every retrieval benchmark that exists is built the other way round. Documents
are collected, queries are written, and an assessor marks which documents
answer which query, usually over a pool produced by the systems being compared.
The consequence is structural: a passage the pooling missed is not merely
unlabelled, it is labelled *irrelevant*. Any system that finds it is penalised
for being right. The size of that bias is unknown and unknowable from inside
the benchmark, and it is largest exactly where it hurts most — for systems that
differ most from the ones used to build the pool.

For chunker comparison the problem is worse still. Assessors label documents.
Chunkers produce fragments of documents. There is no assessor judgement about
whether a *fragment* answers a query, so the standard practice is to inherit
the document's label. Section 2 of the report measures what that inheritance
costs.

## Decision

Declare the facts first as structured data, then render documents from them.

A `Family` is a topic with a value that varies by plan or by region. A `Fact`
is a family paired with one qualifier. Six layout builders render facts into
documents — a plan matrix, topic guides, an FAQ, dense prose, a table, and
runbooks — and each rendering records a `Placement`: the character span of the
sentence stating the value, plus the spans of any headings the value depends
on for its meaning.

Relevance is then not a judgement. It is a set operation on character offsets:
a chunk answers a query if a single chunk contains every required span of some
placement of a target fact.

## Consequences

**What this buys.** The negative labels are as trustworthy as the positive
ones, which is what makes `misleading@k` possible at all — it counts chunks
that are known-wrong, not merely un-marked. Span-level labels make chunker
comparison meaningful. And the whole thing is reproducible by anyone with the
repository and no API keys.

**What it costs, stated plainly.** The corpus is not natural text. Its
vocabulary is smaller than a real corpus, its sentences are more uniform, and
its distractors were written by a generator that knows what a distractor should
look like. Absolute scores here mean nothing; only the differences between
configurations measured on identical material mean anything, and even those
transfer only as far as the generator's assumptions hold. Section 6 contains a
concrete instance of the limit: latent semantic indexing fails on the
`implicit` query class partly because the generator had no reason to place
"largest accounts" and "Enterprise" in the same chunk, which a human-written
corpus would sometimes do.

**A specific hazard the design creates.** Because the labels are complete by
construction, any correct answer that the generator produces *accidentally* —
outside a labelled placement — is an unlabelled positive, exactly the defect
this ADR set out to avoid. That is not hypothetical: the fact table originally
contained the value `"4"`, and a generated near-miss sentence reading "the
default fan-out is 4" would have been a genuine unlabelled answer for any
bag-of-words retriever. Three invariants are enforced in code and by tests to
close it (`assert_values_are_distinctive`, `assert_no_leaked_values`,
`assert_queries_do_not_quote_answers`). See
`docs/portfolio/04-bugs-the-experiment-found.md`.

## Alternatives considered

**Use BEIR or MS MARCO.** Rejected: document-level labels, so the central
question cannot be asked. Section 2 is the measurement of why.

**Collect a corpus and label it by hand.** Rejected on cost, but also on
correctness — hand labelling reintroduces exactly the incompleteness the design
exists to remove, at a scale where it cannot be audited.

**Use an LLM as the assessor.** Rejected. It makes the labels a function of a
model, so a chunker that suits that model's biases scores better, and the
result becomes a statement about the judge. It also cannot run offline, which
kills reproducibility.
