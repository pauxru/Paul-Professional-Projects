# ADR 0002 — Hybrid retrieval (BM25 + dense) fused with RRF

**Status:** Accepted &nbsp; **Date:** 2025 &nbsp; **Owner:** Retrieval

## Context

Retrieval is the load-bearing step in RAG: nothing downstream can recover from
missing evidence. We need a mode that survives both keyword-style questions
("What is the dinner reimbursement cap?") and paraphrase-style questions
("How much can I claim for a business dinner?") without silently degrading
when one signal fails.

## Decision

We implement three retrieval modes and expose them through the same
`IRetriever` contract:

- **Keyword** — a hand-built inverted index with BM25 scoring
  (`Bm25Index.cs`, k₁=1.2, b=0.75, no library dependency).
- **Dense** — cosine similarity over `LocalDeterministicEmbeddingModel`
  vectors, brute-force (see ADR 0003 for why brute force is intentional).
- **Hybrid** — Reciprocal Rank Fusion of the keyword and dense rankings, then
  a lexical-overlap re-ranker over the fused top list.

**Reciprocal Rank Fusion (RRF)** is chosen over score normalisation because
scores from BM25 and cosine are on different scales and normalising them
requires per-corpus tuning. RRF only uses ranks, is parameter-light (one
`k` constant, default 60), and is a well-documented industry pattern.

**Hybrid is the default** even though the eval numbers on our tiny corpus
show that pure keyword wins on ordering. See `docs/evaluation.md` for the
honest interpretation: hybrid degrades gracefully when either signal is
weak, whereas keyword-only collapses on paraphrase and dense-only collapses
on rare tokens (product names, SKUs, incident IDs).

## Consequences

**Positive.**

- Three modes are directly comparable through the golden dataset and the eval
  harness (`RunEvaluation_ReportsMetricsAndHybridBeatsDenseOnMrr`).
- No library dependency: the BM25 implementation is auditable in one file.
- Adding a new mode (e.g., cross-encoder re-rank) is a matter of a new
  `IRetriever` implementation + a new enum value.

**Negative.**

- BM25 parameter tuning is not automated; k₁/b are compile-time constants.
- RRF ignores absolute scores; if one system is dramatically better it still
  gets 50 % of the say. This is acceptable given our modest corpus size.

## Alternatives considered

- **CombSUM / CombMNZ.** Require score normalisation.
- **Learned fusion (e.g., linear regression on rank features).** Requires
  labelled training data we do not have at project start.
- **Reader-only (extract from top-k without re-rank).** Loses the ability
  to prefer paragraphs with both high lexical overlap and high semantic
  similarity.
