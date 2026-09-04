"""Coverage: which facts a chunking survives, decided before retrieval runs.

This is the module that makes the lab different from a leaderboard.

A labelled answer requires a set of spans -- the sentence stating the value,
plus any heading it depends on. A chunk covers a fact if some single chunk
contains all of them. Iterate that over a chunking and you get the set of facts
that are *reachable at all*, which is an upper bound on what any retriever,
reranker or generator built on that chunking can achieve.

The bound is not a statistical estimate. It is a property of the chunking and
the labels, computed exactly, in advance, at a cost of one pass over the index.
"""

from __future__ import annotations

from dataclasses import dataclass

from .chunking import Chunk
from .corpus import Corpus


@dataclass(frozen=True)
class Coverage:
    """Which chunks can answer which facts, under one chunking."""

    #: fid -> chunk ids that contain every required span of some placement.
    by_fact: dict[str, frozenset[str]]
    #: fid -> the layouts through which it survived. Diagnostic only.
    layouts: dict[str, frozenset[str]]
    n_chunks: int

    def reachable(self, fid: str) -> bool:
        return bool(self.by_fact.get(fid))

    def relevant_chunks(self, fid: str) -> frozenset[str]:
        return self.by_fact.get(fid, frozenset())

    def ceiling(self, fids: tuple[str, ...]) -> float:
        if not fids:
            return 0.0
        return sum(1 for f in fids if self.reachable(f)) / len(fids)


def compute_coverage(corpus: Corpus, chunks: list[Chunk]) -> Coverage:
    by_doc: dict[str, list[Chunk]] = {}
    for ch in chunks:
        by_doc.setdefault(ch.doc_id, []).append(ch)

    by_fact: dict[str, set[str]] = {}
    layouts: dict[str, set[str]] = {}
    for p in corpus.placements:
        for ch in by_doc.get(p.doc_id, ()):
            if p.covered_by(ch.spans):
                by_fact.setdefault(p.fid, set()).add(ch.chunk_id)
                layouts.setdefault(p.fid, set()).add(p.layout)

    return Coverage(
        by_fact={k: frozenset(v) for k, v in by_fact.items()},
        layouts={k: frozenset(v) for k, v in layouts.items()},
        n_chunks=len(chunks),
    )


def partial_coverage(corpus: Corpus, chunks: list[Chunk]) -> dict[str, frozenset[str]]:
    """Chunks holding the answer sentence but *not* its required context.

    These are the dangerous ones. They contain the value, so they rank well and
    look like a hit to a document-level judge, but they do not say which plan
    or region the value applies to. A generator handed one of these produces a
    confident answer that is wrong for two thirds of the readers who asked.

    Counting them separately is the difference between "the chunker lost the
    answer" and "the chunker kept the answer and threw away its meaning".
    """
    by_doc: dict[str, list[Chunk]] = {}
    for ch in chunks:
        by_doc.setdefault(ch.doc_id, []).append(ch)

    out: dict[str, set[str]] = {}
    for p in corpus.placements:
        if not p.context:
            continue
        for ch in by_doc.get(p.doc_id, ()):
            if p.answer.covered_by(ch.spans) and not p.covered_by(ch.spans):
                out.setdefault(p.fid, set()).add(ch.chunk_id)
    return {k: frozenset(v) for k, v in out.items()}


def placement_survival(corpus: Corpus, chunks: list[Chunk]) -> dict[str, tuple[int, int, int]]:
    """Per layout: (fully covered, severed, total) placements.

    "Severed" means some chunk holds the answer sentence while no chunk holds
    the answer *and* its required context. It is the distinguishing evidence
    between the two ways a chunking can fail, and the two call for opposite
    fixes: a chunking that loses the sentence needs smaller units, a chunking
    that severs it from its heading needs larger ones or propagated context.
    """
    by_doc: dict[str, list[Chunk]] = {}
    for ch in chunks:
        by_doc.setdefault(ch.doc_id, []).append(ch)

    out: dict[str, list[int]] = {}
    for p in corpus.placements:
        row = out.setdefault(p.layout, [0, 0, 0])
        row[2] += 1
        full = any(p.covered_by(ch.spans) for ch in by_doc.get(p.doc_id, ()))
        if full:
            row[0] += 1
            continue
        if any(p.answer.covered_by(ch.spans) for ch in by_doc.get(p.doc_id, ())):
            row[1] += 1
    return {k: (v[0], v[1], v[2]) for k, v in out.items()}


def document_level_relevance(corpus: Corpus, chunks: list[Chunk]) -> dict[str, frozenset[str]]:
    """What a conventionally-labelled benchmark would call relevant.

    Almost every public retrieval benchmark labels relevance at document level:
    an assessor marks a document as answering a query, and any chunk of that
    document inherits the label. Reproducing that judging rule here, over the
    identical runs, isolates one variable -- the granularity of the labels --
    and shows what it costs.
    """
    docs_for_fact: dict[str, set[str]] = {}
    for p in corpus.placements:
        docs_for_fact.setdefault(p.fid, set()).add(p.doc_id)

    out: dict[str, set[str]] = {}
    for ch in chunks:
        for fid, doc_ids in docs_for_fact.items():
            if ch.doc_id in doc_ids:
                out.setdefault(fid, set()).add(ch.chunk_id)
    return {k: frozenset(v) for k, v in out.items()}
