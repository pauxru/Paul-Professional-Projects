"""The grid.

Seven chunkers x five retrievers x four rerankers, on one query set, with the
candidate pool computed once per (chunker, retriever) and shared by every
reranker. Sharing the pool is not only cheaper -- it is the only way a
comparison between rerankers is a comparison between rerankers.

Per-query scores are kept as arrays in a fixed query order, because every
statistical claim in the report is paired and pairing requires alignment. An
aggregate mean is computed from those arrays and never accumulated separately;
there is exactly one path from a ranking to a number.
"""

from __future__ import annotations

import time
from dataclasses import dataclass, field

import numpy as np

from .attribution import Attribution, CeilingLadder, attribute_query, ladder_from
from .chunking import CHUNKERS, Chunk, chunk_all
from .corpus import Corpus, Document, build_corpus
from .coverage import (
    Coverage,
    compute_coverage,
    document_level_relevance,
    partial_coverage,
    placement_survival,
)
from .distractors import assert_no_leaked_values, build_distractors
from .facts import assert_values_are_distinctive
from .indexes import (
    RETRIEVERS,
    Analysed,
    BM25,
    Index,
    LSA,
    LinearBlend,
    Ranking,
    ReciprocalRankFusion,
    TfIdf,
    analyse,
)
from .metrics import METRICS, Judgement, evaluate, judge
from .queries import QUERIES, Query, assert_queries_do_not_quote_answers
from .rerank import (
    HAND_WEIGHTS,
    MMR,
    RERANKERS,
    RerankContext,
    feature_matrix,
    fit_coordinate_ascent,
    make_context,
    rank_with_weights,
)

K = 10
POOL_DEPTH = 50


@dataclass(frozen=True)
class Config:
    chunker: str
    retriever: str
    reranker: str

    @property
    def name(self) -> str:
        return f"{self.chunker}/{self.retriever}/{self.reranker}"

    @property
    def short(self) -> str:
        return f"{self.chunker.replace('_', '')[:11]}/{self.retriever}/{self.reranker}"


@dataclass
class Cost:
    n_chunks: int
    index_bytes: int
    query_ops: float
    build_seconds: float


@dataclass
class RunResult:
    config: Config
    metrics: dict[str, np.ndarray]
    attribution: Attribution
    by_class: dict[str, Attribution]
    class_ndcg: dict[str, float]
    ladder: CeilingLadder
    cost: Cost
    #: nDCG under document-level relevance, over the identical rankings.
    ndcg_doclevel: np.ndarray

    def mean(self, metric: str) -> float:
        return float(self.metrics[metric].mean())


@dataclass
class ChunkerBundle:
    name: str
    chunks: list[Chunk]
    analysed: Analysed
    coverage: Coverage
    partial: dict[str, frozenset[str]]
    doclevel: dict[str, frozenset[str]]
    judgements: dict[str, Judgement]
    doc_judgements: dict[str, Judgement]
    context: RerankContext
    indexes: dict[str, Index]
    #: layout -> (intact, severed, total) placements. See coverage.placement_survival.
    survival: dict[str, tuple[int, int, int]]


@dataclass
class LabResults:
    runs: dict[str, RunResult] = field(default_factory=dict)
    bundles: dict[str, ChunkerBundle] = field(default_factory=dict)
    queries: tuple[Query, ...] = QUERIES
    fitted_weights: dict[str, np.ndarray] = field(default_factory=dict)
    #: (chunker, retriever) -> nDCG on the reporting half when the weights were
    #: fitted on that same half. The optimism measurement.
    refit_on_report: dict[str, float] = field(default_factory=dict)
    honest_on_report: dict[str, float] = field(default_factory=dict)

    def by_config(self, chunker: str, retriever: str, reranker: str) -> RunResult:
        return self.runs[Config(chunker, retriever, reranker).name]

    def all(self) -> list[RunResult]:
        return [self.runs[k] for k in sorted(self.runs)]


def build_index_set(a: Analysed) -> dict[str, Index]:
    bm = BM25(a)
    tf = TfIdf(a)
    ls = LSA(a)
    return {
        "bm25": bm,
        "tfidf": tf,
        "lsa": ls,
        "rrf": ReciprocalRankFusion([bm, ls]),
        "blend": LinearBlend(bm, ls),
    }


def build_bundle(name: str, docs: list[Document], corpus: Corpus) -> ChunkerBundle:
    chunks = chunk_all(docs, CHUNKERS[name])
    a = analyse(chunks)
    cov = compute_coverage(corpus, chunks)
    part = partial_coverage(corpus, chunks)
    doclevel = document_level_relevance(corpus, chunks)
    judgements = {q.qid: judge(q, cov, part) for q in QUERIES}
    doc_cov = Coverage(by_fact=doclevel, layouts={}, n_chunks=len(chunks))
    doc_judgements = {q.qid: judge(q, doc_cov, {}) for q in QUERIES}
    indexes = build_index_set(a)
    ctx = make_context(chunks, a, indexes["tfidf"])  # type: ignore[arg-type]
    return ChunkerBundle(
        name, chunks, a, cov, part, doclevel, judgements, doc_judgements, ctx,
        indexes, placement_survival(corpus, chunks),
    )


def _pools(bundle: ChunkerBundle, retriever: str) -> tuple[dict[str, Ranking], float]:
    idx = bundle.indexes[retriever]
    pools: dict[str, Ranking] = {}
    ops = 0
    for q in QUERIES:
        pools[q.qid] = idx.search(q.text, POOL_DEPTH)
        ops += idx.last_ops
    return pools, ops / len(QUERIES)


def run_grid(verbose: bool = True) -> LabResults:
    assert_values_are_distinctive()
    assert_queries_do_not_quote_answers()
    corpus = build_corpus()
    distractors = build_distractors()
    assert_no_leaked_values(distractors)
    docs = list(corpus.documents) + list(distractors)

    results = LabResults()
    for cname in CHUNKERS:
        t0 = time.perf_counter()
        bundle = build_bundle(cname, docs, corpus)
        build_s = time.perf_counter() - t0
        results.bundles[cname] = bundle
        if verbose:
            print(f"  chunker {cname}: {len(bundle.chunks)} chunks "
                  f"({build_s:.1f}s)", flush=True)

        for rname in RETRIEVERS:
            pools, mean_ops = _pools(bundle, rname)
            feats = {
                q.qid: feature_matrix(q.text, pools[q.qid], bundle.context)
                for q in QUERIES
            }

            tune = [
                (feats[q.qid], list(pools[q.qid].chunk_ids), bundle.judgements[q.qid])
                for q in QUERIES
                if q.split() == "tune"
            ]
            report_half = [
                (q, feats[q.qid], list(pools[q.qid].chunk_ids), bundle.judgements[q.qid])
                for q in QUERIES
                if q.split() == "report"
            ]
            weights, _ = fit_coordinate_ascent(tune, k=K)
            key = f"{cname}/{rname}"
            results.fitted_weights[key] = weights

            # The optimism measurement: fit on the reporting half, then report
            # on it. Not a configuration anyone would ship -- it is the number
            # that a tuned-and-reported-on-the-same-set pipeline produces.
            refit, _ = fit_coordinate_ascent(
                [(m, ids, j) for _, m, ids, j in report_half], k=K
            )
            results.refit_on_report[key] = float(
                np.mean([
                    evaluate(rank_with_weights(m, ids, refit, K), j, K)[f"ndcg@{K}"]
                    for _, m, ids, j in report_half
                ])
            )
            results.honest_on_report[key] = float(
                np.mean([
                    evaluate(rank_with_weights(m, ids, weights, K), j, K)[f"ndcg@{K}"]
                    for _, m, ids, j in report_half
                ])
            )

            mmr = MMR()
            for rrname in RERANKERS:
                per: dict[str, list[float]] = {m: [] for m in METRICS}
                doc_ndcg: list[float] = []
                total = Attribution()
                by_class: dict[str, Attribution] = {}
                class_scores: dict[str, list[float]] = {}
                for q in QUERIES:
                    pool = pools[q.qid]
                    j = bundle.judgements[q.qid]
                    if rrname == "none":
                        ranked = list(pool.chunk_ids[:K])
                    elif rrname == "mmr":
                        ranked = mmr.rank(q.text, pool, bundle.context, K)
                    elif rrname == "features":
                        ranked = rank_with_weights(
                            feats[q.qid], list(pool.chunk_ids), HAND_WEIGHTS, K
                        )
                    else:
                        ranked = rank_with_weights(
                            feats[q.qid], list(pool.chunk_ids), weights, K
                        )

                    scores = evaluate(ranked, j, K)
                    for m in METRICS:
                        per[m].append(scores[m])
                    class_scores.setdefault(q.cls, []).append(scores[f"ndcg@{K}"])

                    dj = bundle.doc_judgements[q.qid]
                    doc_ndcg.append(evaluate(ranked, dj, K)[f"ndcg@{K}"])

                    a = attribute_query(q, j, list(pool.chunk_ids), ranked, K)
                    total.add(a)
                    by_class.setdefault(q.cls, Attribution()).add(a)

                cfg = Config(cname, rname, rrname)
                results.runs[cfg.name] = RunResult(
                    config=cfg,
                    metrics={m: np.array(per[m], dtype=np.float64) for m in METRICS},
                    attribution=total,
                    by_class=by_class,
                    class_ndcg={
                        c: float(np.mean(v)) for c, v in sorted(class_scores.items())
                    },
                    ladder=ladder_from(total),
                    cost=Cost(
                        n_chunks=len(bundle.chunks),
                        index_bytes=bundle.indexes[rname].bytes_resident,
                        query_ops=mean_ops,
                        build_seconds=build_s,
                    ),
                    ndcg_doclevel=np.array(doc_ndcg, dtype=np.float64),
                )
    return results
