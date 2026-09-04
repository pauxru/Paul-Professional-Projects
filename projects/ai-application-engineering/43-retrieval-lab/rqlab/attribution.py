"""Attribution: which stage lost the answer.

"Our RAG isn't working" is a statement about a pipeline, and a pipeline can
fail in four places. Tuning the wrong one is the default outcome, because the
aggregate score gives no indication of which one is responsible.

For a single (query, target) pair exactly one of these holds, and they are
tested in the order in which the pipeline could have destroyed the answer:

    chunker   no chunk in the entire index covers the target
    retriever a covering chunk exists but none reached the candidate pool
    ranker    a covering chunk reached the pool but not the top k
    served    a covering chunk is in the top k

Because the cases are exclusive and exhaustive,

    served + chunker + retriever + ranker = 1

exactly, for every configuration. There is no residual and nothing to
apportion. That is the property that makes the decomposition worth trusting:
a bug in it does not produce a plausible-looking split, it produces a column
that does not sum to one, and a test asserts that it does.
"""

from __future__ import annotations

from dataclasses import dataclass, field

from .metrics import Judgement
from .queries import Query

STAGES = ("served", "chunker", "retriever", "ranker")


@dataclass
class Attribution:
    counts: dict[str, int] = field(
        default_factory=lambda: {s: 0 for s in STAGES}
    )

    @property
    def total(self) -> int:
        return sum(self.counts.values())

    def share(self) -> dict[str, float]:
        t = self.total
        if t == 0:
            return {s: 0.0 for s in STAGES}
        return {s: self.counts[s] / t for s in STAGES}

    def add(self, other: "Attribution") -> None:
        for s in STAGES:
            self.counts[s] += other.counts[s]


def attribute_query(
    q: Query,
    j: Judgement,
    pool: list[str],
    ranked: list[str],
    k: int,
) -> Attribution:
    """Assign every target of one query to exactly one stage.

    ``pool`` is the candidate set the reranker was given; ``ranked`` is the
    final order. Passing both is what separates "retrieval never surfaced it"
    from "reranking pushed it down", which are different bugs with different
    fixes and are routinely conflated.
    """
    a = Attribution()
    pool_set = set(pool)
    top = set(ranked[:k])
    for fid, covering in j.per_target.items():
        if not covering:
            a.counts["chunker"] += 1
        elif top & covering:
            a.counts["served"] += 1
        elif pool_set & covering:
            a.counts["ranker"] += 1
        else:
            a.counts["retriever"] += 1
    return a


def check_exhaustive(a: Attribution, expected_targets: int) -> None:
    if a.total != expected_targets:
        raise AssertionError(
            f"attribution covered {a.total} targets, expected {expected_targets}"
        )


@dataclass
class CeilingLadder:
    """The three ceilings a configuration sits under, as a decreasing sequence.

    Each is an upper bound on the final score that the corresponding stage has
    already fixed. Reading them together tells you which stage is binding --
    and therefore which one is worth an engineer's week.
    """

    reachable: float  # fraction of targets a chunk covers at all
    pooled: float  # fraction whose covering chunk reached the pool
    served: float  # fraction served in the top k

    def binding_stage(self) -> str:
        gaps = {
            "chunker": 1.0 - self.reachable,
            "retriever": self.reachable - self.pooled,
            "ranker": self.pooled - self.served,
        }
        return max(gaps, key=lambda s: (gaps[s], s))

    def as_row(self) -> tuple[float, float, float]:
        return (self.reachable, self.pooled, self.served)


def ladder_from(total: Attribution) -> CeilingLadder:
    t = total.total or 1
    served = total.counts["served"]
    ranker = total.counts["ranker"]
    retriever = total.counts["retriever"]
    return CeilingLadder(
        reachable=(served + ranker + retriever) / t,
        pooled=(served + ranker) / t,
        served=served / t,
    )
