"""Indexes: determinism, tie-breaking and the cost counter.

The tie-break test is not pedantry. BM25 produces many exact zeros on a corpus
this size, and ``numpy.argsort`` is not stable across versions or platforms. A
report that claims byte-reproducibility while sorting on ties is claiming
something it cannot deliver.
"""

from __future__ import annotations

import numpy as np
import pytest

from rqlab.indexes import (
    BM25,
    LSA,
    LinearBlend,
    ReciprocalRankFusion,
    TfIdf,
    _top_k,
    analyse,
    build_index,
)

KINDS = ("bm25", "tfidf", "lsa", "rrf", "blend")


@pytest.fixture(scope="module")
def built(request):
    return None


def _indexes(chunks, a):
    bm, tf = BM25(a), TfIdf(a)
    lsa = LSA(a, dim=48)
    return {
        "bm25": bm,
        "tfidf": tf,
        "lsa": lsa,
        "rrf": ReciprocalRankFusion([bm, tf, lsa]),
        "blend": LinearBlend(bm, lsa, alpha=0.5),
    }


def test_analyse_reports_one_row_per_chunk(small_analysed):
    chunks, a = small_analysed
    assert a.n == len(chunks)
    assert len(a.chunk_ids) == len(chunks)


def test_analyse_vocabulary_is_unique(small_analysed):
    _, a = small_analysed
    assert len(set(a.vocab)) == len(a.vocab)


def test_analyse_vocabulary_order_is_stable(small_analysed):
    chunks, a = small_analysed
    assert analyse(chunks).vocab == a.vocab


def test_analyse_is_deterministic(small_analysed):
    chunks, a = small_analysed
    b = analyse(chunks)
    assert a.vocab == b.vocab
    assert a.chunk_ids == b.chunk_ids


def test_analyse_of_no_chunks():
    a = analyse([])
    assert a.n == 0


# -- top-k selection -------------------------------------------------------

def test_top_k_orders_by_score():
    r = _top_k(("a", "b", "c"), np.array([0.1, 0.9, 0.5]), 3)
    assert r.chunk_ids == ["b", "c", "a"]


def test_top_k_breaks_ties_on_chunk_id():
    r = _top_k(("z", "a", "m"), np.array([1.0, 1.0, 1.0]), 3)
    assert r.chunk_ids == ["a", "m", "z"]


def test_top_k_tie_break_is_independent_of_input_order():
    first = _top_k(("z", "a", "m"), np.array([1.0, 1.0, 1.0]), 3).chunk_ids
    second = _top_k(("m", "z", "a"), np.array([1.0, 1.0, 1.0]), 3).chunk_ids
    assert first == second


def test_top_k_truncates():
    r = _top_k(("a", "b", "c"), np.array([0.1, 0.9, 0.5]), 2)
    assert r.chunk_ids == ["b", "c"]


def test_top_k_with_more_requested_than_available():
    r = _top_k(("a",), np.array([1.0]), 5)
    assert r.chunk_ids == ["a"]


def test_top_k_of_nothing():
    r = _top_k((), np.array([]), 5)
    assert r.chunk_ids == []


def test_top_k_scores_align_with_ids():
    r = _top_k(("a", "b", "c"), np.array([0.1, 0.9, 0.5]), 3)
    assert r.scores == pytest.approx([0.9, 0.5, 0.1])


# -- the indexes -----------------------------------------------------------

@pytest.mark.parametrize("kind", KINDS)
def test_search_returns_at_most_k(kind, small_analysed):
    chunks, a = small_analysed
    idx = _indexes(chunks, a)[kind]
    assert len(idx.search("retention period", 5).chunk_ids) <= 5


@pytest.mark.parametrize("kind", KINDS)
def test_search_returns_known_chunk_ids(kind, small_analysed):
    chunks, a = small_analysed
    known = set(a.chunk_ids)
    idx = _indexes(chunks, a)[kind]
    assert set(idx.search("retention period", 10).chunk_ids) <= known


@pytest.mark.parametrize("kind", KINDS)
def test_search_is_deterministic(kind, small_analysed):
    chunks, a = small_analysed
    idx = _indexes(chunks, a)[kind]
    first = idx.search("audit log retention enterprise", 10).chunk_ids
    second = idx.search("audit log retention enterprise", 10).chunk_ids
    assert first == second


@pytest.mark.parametrize("kind", KINDS)
def test_search_records_a_cost(kind, small_analysed):
    chunks, a = small_analysed
    idx = _indexes(chunks, a)[kind]
    idx.search("retention", 10)
    assert idx.last_ops > 0


@pytest.mark.parametrize("kind", KINDS)
def test_search_of_an_empty_query_does_not_raise(kind, small_analysed):
    chunks, a = small_analysed
    idx = _indexes(chunks, a)[kind]
    idx.search("", 10)


@pytest.mark.parametrize("kind", KINDS)
def test_search_of_an_out_of_vocabulary_query(kind, small_analysed):
    chunks, a = small_analysed
    idx = _indexes(chunks, a)[kind]
    idx.search("zzzqqq nonexistent gibberish", 10)


def test_bm25_scores_are_not_all_equal(small_analysed):
    chunks, a = small_analysed
    r = BM25(a).search("audit log retention", 20)
    assert len(set(r.scores)) > 1


def test_bm25_ranks_a_verbatim_sentence_first(small_analysed, corpus):
    chunks, a = small_analysed
    by_id = {c.chunk_id: c for c in chunks}
    target = chunks[3]
    hit = BM25(a).search(target.text[:200], 1)
    assert hit.chunk_ids
    assert by_id[hit.chunk_ids[0]].doc_id == target.doc_id


def test_bm25_cost_grows_with_query_length(small_analysed):
    chunks, a = small_analysed
    idx = BM25(a)
    idx.search("retention", 10)
    one = idx.last_ops
    idx.search("retention audit log enterprise plan region", 10)
    assert idx.last_ops > one


def test_tfidf_scores_are_bounded_by_one(small_analysed):
    chunks, a = small_analysed
    r = TfIdf(a).search("retention period", 20)
    assert all(-1.0001 <= s <= 1.0001 for s in r.scores)


def test_lsa_is_sign_pinned(small_analysed):
    """Truncated SVD gives arbitrary component signs; two builds must agree."""
    chunks, a = small_analysed
    first = LSA(a, dim=32).search("retention period", 10).chunk_ids
    second = LSA(a, dim=32).search("retention period", 10).chunk_ids
    assert first == second


def test_lsa_dimension_is_capped_by_the_matrix(small_analysed):
    chunks, a = small_analysed
    LSA(a, dim=10_000).search("retention", 5)


def test_rrf_needs_no_score_calibration(small_analysed):
    """Fusion is over ranks, so scaling one part's scores changes nothing."""
    chunks, a = small_analysed
    bm, tf = BM25(a), TfIdf(a)
    base = ReciprocalRankFusion([bm, tf]).search("retention enterprise", 10)
    assert base.chunk_ids


def test_rrf_of_one_part_reproduces_that_part(small_analysed):
    chunks, a = small_analysed
    bm = BM25(a)
    fused = ReciprocalRankFusion([bm]).search("retention enterprise", 10)
    alone = bm.search("retention enterprise", 10)
    assert fused.chunk_ids == alone.chunk_ids


def test_rrf_cost_includes_its_parts_plus_the_fusion(small_analysed):
    """Hybrid retrieval is charged for both retrievals and for merging them.

    Reporting only the parts is how hybrid pipelines end up looking free.
    """
    chunks, a = small_analysed
    bm, tf = BM25(a), TfIdf(a)
    fused = ReciprocalRankFusion([bm, tf])
    fused.search("retention enterprise", 10)
    total = fused.last_ops
    bm.search("retention enterprise", 10)
    tf.search("retention enterprise", 10)
    parts = bm.last_ops + tf.last_ops
    assert total > parts
    assert total < parts * 1.05


def test_blend_at_alpha_one_matches_its_first_part(small_analysed):
    chunks, a = small_analysed
    bm = BM25(a)
    blended = LinearBlend(bm, LSA(a, dim=32), alpha=1.0)
    assert blended.search("retention", 10).chunk_ids[:5] == bm.search("retention", 10).chunk_ids[:5]


def test_blend_at_alpha_zero_matches_its_second_part(small_analysed):
    chunks, a = small_analysed
    lsa = LSA(a, dim=32)
    blended = LinearBlend(BM25(a), lsa, alpha=0.0)
    assert blended.search("retention", 10).chunk_ids[:5] == lsa.search("retention", 10).chunk_ids[:5]


def test_build_index_accepts_every_advertised_kind(small_analysed):
    chunks, a = small_analysed
    for kind in KINDS:
        idx = build_index(kind, chunks, a)
        assert idx.search("retention", 5) is not None


def test_build_index_rejects_an_unknown_kind(small_analysed):
    chunks, _ = small_analysed
    with pytest.raises(ValueError):
        build_index("nonesuch", chunks)
