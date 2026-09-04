"""Retrieval indexes.

Five scorers over the same analysed tokens, so that a difference between them
is a difference in scoring rather than in tokenisation.

A detail that matters and is easy to miss: inverse document frequency is
computed over *chunks*, so changing the chunker changes every IDF weight in the
index. Chunker and retriever are therefore not independent factors, and the
experiment treats their combination as the unit rather than pretending the
effects add.
"""

from __future__ import annotations

import math
import time
from collections import Counter
from dataclasses import dataclass

import numpy as np

from .chunking import Chunk
from .text import analysed


@dataclass
class Analysed:
    """The analysed index, shared by every scorer so they see identical text."""

    chunk_ids: tuple[str, ...]
    docs: tuple[tuple[str, ...], ...]
    vocab: dict[str, int]
    df: np.ndarray
    lengths: np.ndarray
    avg_len: float

    @property
    def n(self) -> int:
        return len(self.chunk_ids)


def analyse(chunks: list[Chunk]) -> Analysed:
    ids = tuple(ch.chunk_id for ch in chunks)
    docs = tuple(tuple(analysed(ch.text)) for ch in chunks)
    vocab: dict[str, int] = {}
    for d in docs:
        for t in d:
            if t not in vocab:
                vocab[t] = len(vocab)
    df = np.zeros(len(vocab), dtype=np.float64)
    for d in docs:
        for t in set(d):
            df[vocab[t]] += 1.0
    lengths = np.array([len(d) for d in docs], dtype=np.float64)
    avg = float(lengths.mean()) if len(lengths) else 0.0
    return Analysed(ids, docs, vocab, df, lengths, avg)


def _counts_matrix(a: Analysed) -> np.ndarray:
    m = np.zeros((a.n, len(a.vocab)), dtype=np.float64)
    for i, d in enumerate(a.docs):
        for t, c in Counter(d).items():
            m[i, a.vocab[t]] = float(c)
    return m


@dataclass
class Ranking:
    chunk_ids: list[str]
    scores: list[float]


class Index:
    name: str = "index"
    build_seconds: float = 0.0
    bytes_resident: int = 0
    #: Multiply-accumulate operations charged by the most recent search.
    #: Counted rather than timed: wall-clock on a shared machine measures the
    #: machine, and a cost axis that changes between runs cannot appear on a
    #: reproducible Pareto frontier.
    last_ops: int = 0

    def search(self, query: str, k: int) -> Ranking:  # pragma: no cover - interface
        raise NotImplementedError


def _top_k(ids: tuple[str, ...], scores: np.ndarray, k: int) -> Ranking:
    """Rank by score, breaking ties on chunk id.

    Ties are common -- BM25 gives many chunks exactly zero -- and numpy's sort
    order for equal keys is not something to rely on across versions. Without
    an explicit tie-break, two runs of the same configuration can differ in
    nDCG purely through the order of chunks that scored identically, which is
    indistinguishable in the output from a real effect.
    """
    k = min(k, len(ids))
    if k == 0:
        return Ranking([], [])
    order = sorted(range(len(ids)), key=lambda i: (-float(scores[i]), ids[i]))[:k]
    return Ranking([ids[i] for i in order], [float(scores[i]) for i in order])


class BM25(Index):
    """Okapi BM25. The baseline that most vector-search comparisons omit."""

    name = "bm25"

    def __init__(self, a: Analysed, k1: float = 1.2, b: float = 0.75) -> None:
        t0 = time.perf_counter()
        self.a = a
        self.k1 = k1
        self.b = b
        self.idf = np.log(1.0 + (a.n - a.df + 0.5) / (a.df + 0.5))
        self.postings: list[dict[int, float]] = []
        norm = k1 * (1.0 - b + b * (a.lengths / (a.avg_len or 1.0)))
        for i, d in enumerate(a.docs):
            row: dict[int, float] = {}
            for t, c in Counter(d).items():
                j = a.vocab[t]
                row[j] = (c * (k1 + 1.0)) / (c + norm[i])
            self.postings.append(row)
        self.inverted: dict[int, list[tuple[int, float]]] = {}
        for i, row in enumerate(self.postings):
            for j, v in row.items():
                self.inverted.setdefault(j, []).append((i, v))
        self.build_seconds = time.perf_counter() - t0
        self.bytes_resident = sum(len(v) for v in self.inverted.values()) * 16

    def search(self, query: str, k: int) -> Ranking:
        scores = np.zeros(self.a.n, dtype=np.float64)
        ops = 0
        for t in analysed(query):
            j = self.a.vocab.get(t)
            if j is None:
                continue
            w = self.idf[j]
            posting = self.inverted.get(j, ())
            ops += len(posting)
            for i, v in posting:
                scores[i] += w * v
        self.last_ops = ops
        return _top_k(self.a.chunk_ids, scores, k)


class TfIdf(Index):
    """Cosine over log-tf * idf vectors, L2 normalised."""

    name = "tfidf"

    def __init__(self, a: Analysed) -> None:
        t0 = time.perf_counter()
        self.a = a
        self.idf = np.log((1.0 + a.n) / (1.0 + a.df)) + 1.0
        m = _counts_matrix(a)
        with np.errstate(divide="ignore"):
            m = np.where(m > 0, 1.0 + np.log(m), 0.0)
        m *= self.idf
        norms = np.linalg.norm(m, axis=1, keepdims=True)
        norms[norms == 0.0] = 1.0
        self.m = m / norms
        self.build_seconds = time.perf_counter() - t0
        self.bytes_resident = self.m.nbytes

    def _qvec(self, query: str) -> np.ndarray:
        v = np.zeros(len(self.a.vocab), dtype=np.float64)
        for t, c in Counter(analysed(query)).items():
            j = self.a.vocab.get(t)
            if j is not None:
                v[j] = (1.0 + math.log(c)) * self.idf[j]
        n = np.linalg.norm(v)
        return v / n if n else v

    def search(self, query: str, k: int) -> Ranking:
        self.last_ops = int(self.m.shape[0]) * int(self.m.shape[1])
        return _top_k(self.a.chunk_ids, self.m @ self._qvec(query), k)


class LSA(Index):
    """Truncated SVD of the TF-IDF matrix; queries folded into the same space.

    Stands in for a dense embedding model. It is genuinely distributional --
    two terms that co-occur become close without ever appearing in the same
    chunk -- which is the property being tested, and unlike a hosted model it
    is fully deterministic and inspectable.
    """

    name = "lsa"

    def __init__(self, a: Analysed, dim: int = 160) -> None:
        t0 = time.perf_counter()
        self.base = TfIdf(a)
        self.a = a
        d = min(dim, min(self.base.m.shape) - 1)
        # Sign convention: numpy's SVD may return either sign for a component.
        # Cosine similarity is invariant to a consistent flip, but pinning it
        # makes stored vectors comparable across runs and keeps the report
        # byte-reproducible.
        u, s, vt = np.linalg.svd(self.base.m, full_matrices=False)
        v = vt[:d].T
        flip = np.where(v[np.argmax(np.abs(v), axis=0), np.arange(d)] < 0, -1.0, 1.0)
        self.v = v * flip
        self.dim = d
        emb = self.base.m @ self.v
        norms = np.linalg.norm(emb, axis=1, keepdims=True)
        norms[norms == 0.0] = 1.0
        self.emb = emb / norms
        self.build_seconds = time.perf_counter() - t0
        self.bytes_resident = self.emb.nbytes + self.v.nbytes

    def search(self, query: str, k: int) -> Ranking:
        q = self.base._qvec(query) @ self.v
        n = np.linalg.norm(q)
        if n:
            q = q / n
        self.last_ops = int(self.v.shape[0]) * self.dim + int(self.emb.shape[0]) * self.dim
        return _top_k(self.a.chunk_ids, self.emb @ q, k)


class ReciprocalRankFusion(Index):
    """Fuse rankings by rank, not by score.

    The reason to prefer it over score interpolation is that it needs no
    calibration: BM25 scores are unbounded and corpus-dependent while cosine
    lives in [-1, 1], so any fixed interpolation weight is really a statement
    about the corpus.
    """

    name = "rrf"

    def __init__(self, parts: list[Index], k0: int = 60, depth: int = 200) -> None:
        self.parts = parts
        self.k0 = k0
        self.depth = depth
        self.build_seconds = sum(p.build_seconds for p in parts)
        self.bytes_resident = sum(p.bytes_resident for p in parts)

    def search(self, query: str, k: int) -> Ranking:
        acc: dict[str, float] = {}
        ops = 0
        for p in self.parts:
            r = p.search(query, self.depth)
            ops += p.last_ops + len(r.chunk_ids)
            for rank, cid in enumerate(r.chunk_ids):
                acc[cid] = acc.get(cid, 0.0) + 1.0 / (self.k0 + rank + 1)
        self.last_ops = ops
        order = sorted(acc.items(), key=lambda kv: (-kv[1], kv[0]))[:k]
        return Ranking([c for c, _ in order], [s for _, s in order])


class LinearBlend(Index):
    """Interpolate min-max normalised scores over the union of two candidate sets.

    Included specifically so the report can show what it costs relative to
    rank fusion, rather than asserting that fusion is better.
    """

    name = "blend"

    def __init__(self, a: Index, b: Index, alpha: float = 0.5, depth: int = 200) -> None:
        self.a = a
        self.b = b
        self.alpha = alpha
        self.depth = depth
        self.build_seconds = a.build_seconds + b.build_seconds
        self.bytes_resident = a.bytes_resident + b.bytes_resident

    @staticmethod
    def _norm(r: Ranking) -> dict[str, float]:
        if not r.scores:
            return {}
        lo, hi = min(r.scores), max(r.scores)
        rng = hi - lo
        if rng <= 0.0:
            return {c: 0.0 for c in r.chunk_ids}
        return {c: (s - lo) / rng for c, s in zip(r.chunk_ids, r.scores)}

    def search(self, query: str, k: int) -> Ranking:
        na = self._norm(self.a.search(query, self.depth))
        ops_a = self.a.last_ops
        nb = self._norm(self.b.search(query, self.depth))
        self.last_ops = ops_a + self.b.last_ops + len(na) + len(nb)
        acc: dict[str, float] = {}
        for c in set(na) | set(nb):
            acc[c] = self.alpha * na.get(c, 0.0) + (1 - self.alpha) * nb.get(c, 0.0)
        order = sorted(acc.items(), key=lambda kv: (-kv[1], kv[0]))[:k]
        return Ranking([c for c, _ in order], [s for _, s in order])


RETRIEVERS = ("bm25", "tfidf", "lsa", "rrf", "blend")


def build_index(kind: str, chunks: list[Chunk], a: Analysed | None = None) -> Index:
    a = a if a is not None else analyse(chunks)
    if kind == "bm25":
        return BM25(a)
    if kind == "tfidf":
        return TfIdf(a)
    if kind == "lsa":
        return LSA(a)
    if kind == "rrf":
        return ReciprocalRankFusion([BM25(a), LSA(a)])
    if kind == "blend":
        return LinearBlend(BM25(a), LSA(a))
    raise ValueError(f"unknown retriever {kind!r}")
