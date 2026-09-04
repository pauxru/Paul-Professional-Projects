"""End-to-end pipeline properties, on a deliberately small slice of the grid.

The full grid is 140 configurations and belongs in ``run_lab.py``. What belongs
here are the invariants that must hold for *every* configuration, checked on
enough of them to be meaningful and few enough to run in seconds.
"""

from __future__ import annotations

import numpy as np
import pytest

from rqlab.attribution import Attribution, attribute_query, ladder_from
from rqlab.experiment import K, POOL_DEPTH, build_bundle, build_index_set
from rqlab.metrics import evaluate
from rqlab.rerank import HAND_WEIGHTS, MMR, feature_matrix, rank_with_weights


@pytest.fixture(scope="module")
def bundle(corpus, distractors):
    docs = list(corpus.documents) + list(distractors)
    return build_bundle("fixed_240", docs, corpus)


@pytest.fixture(scope="module")
def pooled(bundle, queries):
    idx = bundle.indexes["bm25"]
    subset = queries[::6]
    return subset, {q.qid: idx.search(q.text, POOL_DEPTH) for q in subset}


def test_bundle_indexes_cover_every_retriever(bundle):
    assert set(bundle.indexes) == {"bm25", "tfidf", "lsa", "rrf", "blend"}


def test_bundle_judges_every_query(bundle, queries):
    assert set(bundle.judgements) == {q.qid for q in queries}
    assert set(bundle.doc_judgements) == {q.qid for q in queries}


def test_build_index_set_is_deterministic(bundle):
    a = build_index_set(bundle.analysed)
    b = build_index_set(bundle.analysed)
    for kind in a:
        assert a[kind].search("retention enterprise", 10).chunk_ids == \
            b[kind].search("retention enterprise", 10).chunk_ids


def test_pool_depth_is_respected(pooled):
    _, pools = pooled
    for r in pools.values():
        assert len(r.chunk_ids) <= POOL_DEPTH


def test_attribution_is_exhaustive_for_every_query(bundle, pooled):
    subset, pools = pooled
    for q in subset:
        pool = list(pools[q.qid].chunk_ids)
        j = bundle.judgements[q.qid]
        a = attribute_query(q, j, pool, pool[:K], K)
        assert a.total == len(q.targets)


def test_attribution_shares_sum_to_one(bundle, pooled):
    subset, pools = pooled
    total = Attribution()
    for q in subset:
        pool = list(pools[q.qid].chunk_ids)
        total.add(attribute_query(q, bundle.judgements[q.qid], pool, pool[:K], K))
    assert sum(total.share().values()) == pytest.approx(1.0)


def test_ladder_is_monotone_on_real_data(bundle, pooled):
    subset, pools = pooled
    total = Attribution()
    for q in subset:
        pool = list(pools[q.qid].chunk_ids)
        total.add(attribute_query(q, bundle.judgements[q.qid], pool, pool[:K], K))
    lad = ladder_from(total)
    assert lad.reachable >= lad.pooled >= lad.served


def test_a_reranker_cannot_exceed_the_pool_ceiling(bundle, pooled):
    """The claim section 7 rests on, asserted rather than argued."""
    subset, pools = pooled
    for q in subset:
        pool = list(pools[q.qid].chunk_ids)
        ranked = MMR().rank(q.text, pools[q.qid], bundle.context, K)
        assert set(ranked) <= set(pool)


def test_reranking_never_invents_a_chunk(bundle, pooled):
    subset, pools = pooled
    for q in subset:
        mat = feature_matrix(q.text, pools[q.qid], bundle.context)
        out = rank_with_weights(mat, list(pools[q.qid].chunk_ids), HAND_WEIGHTS, K)
        assert set(out) <= set(pools[q.qid].chunk_ids)


def test_metrics_are_in_range_end_to_end(bundle, pooled):
    subset, pools = pooled
    for q in subset:
        ranked = list(pools[q.qid].chunk_ids[:K])
        for name, v in evaluate(ranked, bundle.judgements[q.qid], K).items():
            assert 0.0 <= v <= 1.0, name


def test_document_level_judging_moves_scores_in_both_directions(bundle, pooled):
    """Doc-level labels are a superset, but nDCG is not monotone in that.

    The relevant set grows, so the ideal DCG grows with it: a ranking that
    filled every slot it could under span labels no longer fills every slot the
    ideal now assumes. Per-query scores therefore move both ways, which is a
    stronger statement than "document-level labels inflate scores" -- they do
    not inflate, they *decorrelate*, and a comparison built on them is not a
    weakened version of the right measurement but a different one.
    """
    subset, pools = pooled
    up = down = 0
    for q in subset:
        ranked = list(pools[q.qid].chunk_ids[:K])
        span = evaluate(ranked, bundle.judgements[q.qid], K)[f"ndcg@{K}"]
        doc = evaluate(ranked, bundle.doc_judgements[q.qid], K)[f"ndcg@{K}"]
        if doc > span + 1e-12:
            up += 1
        elif doc < span - 1e-12:
            down += 1
    assert up > 0 and down > 0


def test_document_level_relevant_sets_contain_the_span_level_ones(bundle, queries):
    for q in queries[::6]:
        span = bundle.judgements[q.qid]
        doc = bundle.doc_judgements[q.qid]
        assert span.relevant <= doc.relevant, q.qid


def test_the_whole_pipeline_is_reproducible(corpus, distractors):
    """Two independent builds must produce identical rankings.

    Cheaper than regenerating the report twice, and it fails for the same
    reasons: an unstable sort, an unseeded shuffle, a set iterated in hash
    order.
    """
    docs = list(corpus.documents) + list(distractors)
    a = build_bundle("fixed_240", docs, corpus)
    b = build_bundle("fixed_240", docs, corpus)
    assert [c.chunk_id for c in a.chunks] == [c.chunk_id for c in b.chunks]
    for kind in a.indexes:
        ra = a.indexes[kind].search("retention enterprise plan", 20)
        rb = b.indexes[kind].search("retention enterprise plan", 20)
        assert ra.chunk_ids == rb.chunk_ids, kind
        assert np.allclose(ra.scores, rb.scores), kind


def test_coordinate_ascent_does_not_lose_to_its_starting_point(bundle, pooled):
    from rqlab.rerank import fit_coordinate_ascent

    subset, pools = pooled
    rows = [
        (
            feature_matrix(q.text, pools[q.qid], bundle.context),
            list(pools[q.qid].chunk_ids),
            bundle.judgements[q.qid],
        )
        for q in subset
    ]
    weights, score = fit_coordinate_ascent(rows, k=K)
    base = float(np.mean([
        evaluate(rank_with_weights(m, ids, HAND_WEIGHTS, K), j, K)[f"ndcg@{K}"]
        for m, ids, j in rows
    ]))
    assert score >= base - 1e-9
    assert len(weights) == len(HAND_WEIGHTS)


def test_coordinate_ascent_is_deterministic(bundle, pooled):
    from rqlab.rerank import fit_coordinate_ascent

    subset, pools = pooled
    rows = [
        (
            feature_matrix(q.text, pools[q.qid], bundle.context),
            list(pools[q.qid].chunk_ids),
            bundle.judgements[q.qid],
        )
        for q in subset
    ]
    w1, s1 = fit_coordinate_ascent(rows, k=K)
    w2, s2 = fit_coordinate_ascent(rows, k=K)
    assert np.array_equal(w1, w2)
    assert s1 == s2


def test_cost_counters_are_positive(bundle, pooled):
    subset, _ = pooled
    for kind, idx in bundle.indexes.items():
        idx.search(subset[0].text, POOL_DEPTH)
        assert idx.last_ops > 0, kind
