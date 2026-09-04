"""Metrics, including one that counts harm rather than absence.

The standard set (nDCG, MRR, recall) answers "did the right thing appear?".
None of them answers "did the wrong thing appear instead?", and in a corpus
where the same sentence is correct for one plan and wrong for another, that is
the question that decides whether the system is safe to ship.

``misleading_at_k`` answers it. A chunk is misleading when it holds the right
value under no qualifier, or the right qualifier's neighbour value -- material
that a generator will render into a fluent, cited, wrong answer.
"""

from __future__ import annotations

import math
from dataclasses import dataclass

from .coverage import Coverage
from .queries import Query


@dataclass(frozen=True)
class Judgement:
    """Which chunks are relevant, and which are actively harmful, per query."""

    relevant: frozenset[str]
    misleading: frozenset[str]
    #: fid -> chunks that fully cover it. Needed for per-target recall.
    per_target: dict[str, frozenset[str]]


def judge(
    q: Query,
    cov: Coverage,
    partial: dict[str, frozenset[str]],
) -> Judgement:
    per_target = {fid: cov.relevant_chunks(fid) for fid in q.targets}
    relevant: set[str] = set()
    for s in per_target.values():
        relevant |= s

    harmful: set[str] = set()
    for fid in q.targets:
        harmful |= set(partial.get(fid, ()))
    for fid in q.siblings:
        harmful |= set(cov.relevant_chunks(fid))
        harmful |= set(partial.get(fid, ()))
    # A chunk that answers the question correctly is not misleading, even if it
    # also contains a sibling's value -- a table row states every plan's value
    # next to the label that distinguishes them.
    harmful -= relevant

    return Judgement(frozenset(relevant), frozenset(harmful), per_target)


def dcg(gains: list[float]) -> float:
    return sum(g / math.log2(i + 2) for i, g in enumerate(gains))


def ndcg_at_k(ranked: list[str], j: Judgement, k: int) -> float:
    top = ranked[:k]
    gains = [1.0 if c in j.relevant else 0.0 for c in top]
    ideal = [1.0] * min(k, len(j.relevant))
    denom = dcg(ideal)
    return dcg(gains) / denom if denom else 0.0


def recall_at_k(ranked: list[str], j: Judgement, k: int) -> float:
    """Fraction of *targets* covered by some chunk in the top k.

    Deliberately over targets rather than over relevant chunks. A question with
    one answer is fully served by one good chunk, and a metric that penalises a
    system for not also returning the four redundant copies of it is measuring
    corpus duplication.
    """
    if not j.per_target:
        return 0.0
    top = set(ranked[:k])
    hit = sum(1 for chunks in j.per_target.values() if top & chunks)
    return hit / len(j.per_target)


def answerable_at_k(ranked: list[str], j: Judgement, k: int) -> float:
    """1.0 only when *every* target is covered in the top k."""
    return 1.0 if recall_at_k(ranked, j, k) >= 1.0 else 0.0


def mrr(ranked: list[str], j: Judgement) -> float:
    for i, c in enumerate(ranked):
        if c in j.relevant:
            return 1.0 / (i + 1)
    return 0.0


def misleading_at_k(ranked: list[str], j: Judgement, k: int) -> float:
    top = ranked[:k]
    if not top:
        return 0.0
    return sum(1 for c in top if c in j.misleading) / len(top)


def misleading_first(ranked: list[str], j: Judgement) -> float:
    """1.0 when the top result is harmful.

    Separated from ``misleading_at_k`` because the top result is what a
    single-passage generator is handed, and an average over ten positions can
    look reassuring while rank one is wrong most of the time.
    """
    if not ranked:
        return 0.0
    return 1.0 if ranked[0] in j.misleading else 0.0


def unanswered(ranked: list[str], j: Judgement, k: int) -> float:
    """1.0 when nothing relevant *and* nothing misleading is in the top k.

    Failing loudly. This is the good failure: the system returns material that
    is visibly off-topic, so a human or a generator with any grounding check
    declines to answer. Distinguishing it from the harmful failure is the point
    of measuring both.
    """
    top = set(ranked[:k])
    if top & j.relevant:
        return 0.0
    return 0.0 if top & j.misleading else 1.0


METRICS = (
    "ndcg@10",
    "recall@10",
    "answerable@10",
    "mrr",
    "misleading@10",
    "misleading@1",
    "unanswered@10",
)


def evaluate(ranked: list[str], j: Judgement, k: int = 10) -> dict[str, float]:
    return {
        f"ndcg@{k}": ndcg_at_k(ranked, j, k),
        f"recall@{k}": recall_at_k(ranked, j, k),
        f"answerable@{k}": answerable_at_k(ranked, j, k),
        "mrr": mrr(ranked, j),
        f"misleading@{k}": misleading_at_k(ranked, j, k),
        "misleading@1": misleading_first(ranked, j),
        f"unanswered@{k}": unanswered(ranked, j, k),
    }
