"""Rerankers.

A reranker cannot retrieve. It reorders a candidate pool, so its ceiling is the
pool's recall, and every gain it appears to produce is bounded by how much
useful material retrieval already found. The report measures that relationship
rather than assuming it.

Four are compared:

none        the pool order, truncated
mmr         maximal marginal relevance -- relevance traded against redundancy
features    a hand-weighted linear scorer over interpretable features
fitted      the same features, weights learned by coordinate ascent on nDCG

``fitted`` exists to make one specific point measurable. It is fitted on half
the queries and reported on the other half, and the report also shows what it
would have scored had it been fitted and reported on the same half. The gap
between those two numbers is the amount by which a tuned pipeline flatters
itself, and it is the reason most published retrieval improvements do not
reproduce.
"""

from __future__ import annotations

import math
from collections import Counter
from dataclasses import dataclass

import numpy as np

from .chunking import Chunk
from .indexes import Analysed, Ranking, TfIdf
from .metrics import Judgement, ndcg_at_k
from .text import analysed

FEATURES = (
    "coverage",
    "idf_coverage",
    "proximity",
    "bigram",
    "has_heading",
    "brevity",
    "pool_rank",
)


@dataclass
class RerankContext:
    """Everything a reranker may look at. Deliberately not the labels."""

    analysed: Analysed
    tfidf: TfIdf
    by_id: dict[str, Chunk]
    index_of: dict[str, int]


def make_context(chunks: list[Chunk], a: Analysed, tfidf: TfIdf) -> RerankContext:
    return RerankContext(
        analysed=a,
        tfidf=tfidf,
        by_id={c.chunk_id: c for c in chunks},
        index_of={cid: i for i, cid in enumerate(a.chunk_ids)},
    )


def _features(q_terms: list[str], ctx: RerankContext, cid: str, rank: int) -> np.ndarray:
    i = ctx.index_of[cid]
    doc = ctx.analysed.docs[i]
    doc_set = set(doc)
    present = [t for t in q_terms if t in doc_set]
    coverage = len(present) / len(q_terms) if q_terms else 0.0

    idf = ctx.tfidf.idf
    vocab = ctx.analysed.vocab
    tot = sum(idf[vocab[t]] for t in q_terms if t in vocab) or 1.0
    got = sum(idf[vocab[t]] for t in present if t in vocab)
    idf_cov = got / tot

    # Smallest window of the chunk containing every query term that is present.
    proximity = 0.0
    if len(present) > 1:
        wanted = set(present)
        positions = [i for i, t in enumerate(doc) if t in wanted]
        best = len(doc)
        seen: Counter[str] = Counter()
        left = 0
        for right, pos in enumerate(positions):
            seen[doc[pos]] += 1
            while len(seen) == len(wanted):
                best = min(best, pos - positions[left] + 1)
                seen[doc[positions[left]]] -= 1
                if seen[doc[positions[left]]] == 0:
                    del seen[doc[positions[left]]]
                left += 1
        proximity = len(wanted) / best if best else 0.0
    elif present:
        proximity = 1.0

    qb = set(zip(q_terms, q_terms[1:]))
    db = set(zip(doc, doc[1:]))
    bigram = len(qb & db) / len(qb) if qb else 0.0

    text = ctx.by_id[cid].text
    has_heading = 1.0 if "#" in text else 0.0
    brevity = 1.0 / (1.0 + math.log1p(len(doc)))
    pool_rank = 1.0 / (1.0 + rank)

    return np.array(
        [coverage, idf_cov, proximity, bigram, has_heading, brevity, pool_rank],
        dtype=np.float64,
    )


#: Weights chosen by reading the feature definitions, before seeing any score.
#: Written down in advance so that the fitted weights below can be compared
#: against a genuine prior rather than against a strawman.
HAND_WEIGHTS = np.array([1.0, 1.5, 0.8, 0.6, 0.3, 0.2, 0.5], dtype=np.float64)


def feature_matrix(query: str, pool: Ranking, ctx: RerankContext) -> np.ndarray:
    """Features for every candidate, computed once per (config, query).

    Shared by the hand-weighted and the fitted reranker, and by the fitting
    itself. Beyond saving time this removes a whole class of mistake: the two
    rerankers cannot end up scoring subtly different feature values, so a
    difference between them is a difference in weights and nothing else.
    """
    q_terms = analysed(query)
    if not pool.chunk_ids:
        return np.zeros((0, len(FEATURES)), dtype=np.float64)
    return np.array(
        [_features(q_terms, ctx, cid, r) for r, cid in enumerate(pool.chunk_ids)],
        dtype=np.float64,
    )


def rank_with_weights(
    mat: np.ndarray, ids: list[str], weights: np.ndarray, k: int
) -> list[str]:
    if not ids:
        return []
    s = mat @ weights
    order = sorted(range(len(ids)), key=lambda i: (-float(s[i]), ids[i]))[:k]
    return [ids[i] for i in order]


class Reranker:
    name = "none"

    def rank(self, query: str, pool: Ranking, ctx: RerankContext, k: int) -> list[str]:
        return pool.chunk_ids[:k]


class Identity(Reranker):
    name = "none"


class MMR(Reranker):
    """Trade relevance against redundancy in the returned set.

    Worth including because it is the one reranker that can lower nDCG for a
    principled reason: it deliberately demotes a chunk that duplicates one
    already selected, and in a corpus with genuine redundancy several of those
    duplicates are relevant.
    """

    name = "mmr"

    def __init__(self, lam: float = 0.7) -> None:
        self.lam = lam

    def rank(self, query: str, pool: Ranking, ctx: RerankContext, k: int) -> list[str]:
        ids = pool.chunk_ids
        if not ids:
            return []
        rows = np.array([ctx.index_of[c] for c in ids])
        vecs = ctx.tfidf.m[rows]
        rel = np.array(pool.scores, dtype=np.float64)
        span = rel.max() - rel.min()
        rel = (rel - rel.min()) / span if span > 0 else np.zeros_like(rel)
        sim = vecs @ vecs.T
        chosen: list[int] = []
        remaining = list(range(len(ids)))
        while remaining and len(chosen) < k:
            if not chosen:
                best = max(remaining, key=lambda i: (rel[i], -i))
            else:
                def score(i: int) -> tuple[float, int]:
                    red = max(sim[i, j] for j in chosen)
                    return (self.lam * rel[i] - (1 - self.lam) * red, -i)

                best = max(remaining, key=score)
            chosen.append(best)
            remaining.remove(best)
        return [ids[i] for i in chosen]


class LinearFeatures(Reranker):
    name = "features"

    def __init__(self, weights: np.ndarray | None = None, name: str | None = None) -> None:
        self.weights = HAND_WEIGHTS if weights is None else weights
        if name:
            self.name = name

    def rank(self, query: str, pool: Ranking, ctx: RerankContext, k: int) -> list[str]:
        mat = feature_matrix(query, pool, ctx)
        return rank_with_weights(mat, list(pool.chunk_ids), self.weights, k)


def fit_coordinate_ascent(
    training: list[tuple[np.ndarray, list[str], Judgement]],
    *,
    k: int = 10,
    rounds: int = 3,
    grid: tuple[float, ...] = (-1.0, -0.5, 0.0, 0.25, 0.5, 1.0, 1.5, 2.0, 3.0),
    start: np.ndarray | None = None,
) -> tuple[np.ndarray, float]:
    """Maximise mean nDCG@k by sweeping one weight at a time.

    Coordinate ascent rather than gradient descent because nDCG is a step
    function of the weights -- it changes only when two chunks swap order -- so
    it has no useful gradient anywhere. Ties are resolved towards the incumbent
    weight so the search is deterministic.
    """
    w = HAND_WEIGHTS.copy() if start is None else start.copy()
    cache = training

    def objective(weights: np.ndarray) -> float:
        total = 0.0
        for mat, ids, j in cache:
            if not ids:
                continue
            total += ndcg_at_k(rank_with_weights(mat, ids, weights, k), j, k)
        return total / len(cache) if cache else 0.0

    best = objective(w)
    for _ in range(rounds):
        improved = False
        for f in range(len(w)):
            incumbent = w[f]
            for candidate in grid:
                if candidate == incumbent:
                    continue
                w[f] = candidate
                score = objective(w)
                if score > best + 1e-12:
                    best = score
                    incumbent = candidate
                    improved = True
            w[f] = incumbent
        if not improved:
            break
    return w, best


RERANKERS = ("none", "mmr", "features", "fitted")
