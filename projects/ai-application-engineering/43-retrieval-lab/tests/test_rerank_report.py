"""Reranking and the report writer.

The report tests protect the discipline rather than the prose: a prediction
that is written after the number is read is worthless, and the only way to keep
that honest in a generated document is to make the generator refuse.
"""

from __future__ import annotations

import numpy as np
import pytest

from rqlab.chunking import CHUNKERS, chunk_all
from rqlab.indexes import BM25, TfIdf, analyse
from rqlab.report import Report, num, pct, signed, _wrap
from rqlab.rerank import (
    FEATURES,
    HAND_WEIGHTS,
    Identity,
    LinearFeatures,
    MMR,
    feature_matrix,
    make_context,
    rank_with_weights,
)


@pytest.fixture(scope="module")
def ctx_and_pool(corpus):
    chunks = chunk_all(list(corpus.documents), CHUNKERS["fixed_240"])
    a = analyse(chunks)
    tf = TfIdf(a)
    ctx = make_context(chunks, a, tf)
    pool = BM25(a).search("audit log retention enterprise plan", 30)
    return ctx, pool


def test_hand_weights_have_one_entry_per_feature():
    assert len(HAND_WEIGHTS) == len(FEATURES)


def test_feature_matrix_shape(ctx_and_pool):
    ctx, pool = ctx_and_pool
    m = feature_matrix("audit log retention", pool, ctx)
    assert m.shape == (len(pool.chunk_ids), len(FEATURES))


def test_features_are_finite(ctx_and_pool):
    ctx, pool = ctx_and_pool
    m = feature_matrix("audit log retention", pool, ctx)
    assert np.isfinite(m).all()


def test_features_are_bounded(ctx_and_pool):
    """Unbounded features make a linear model's weights uninterpretable."""
    ctx, pool = ctx_and_pool
    m = feature_matrix("audit log retention", pool, ctx)
    assert m.min() >= -1.0001
    assert m.max() <= 1.0001


def test_coverage_feature_is_high_for_a_verbatim_query(ctx_and_pool):
    ctx, pool = ctx_and_pool
    text = ctx.by_id[pool.chunk_ids[0]].text
    m = feature_matrix(text[:150], pool, ctx)
    assert m[:, FEATURES.index("coverage")].max() > 0.5


def test_pool_rank_feature_decreases_down_the_pool(ctx_and_pool):
    ctx, pool = ctx_and_pool
    col = feature_matrix("retention", pool, ctx)[:, FEATURES.index("pool_rank")]
    assert col[0] >= col[-1]


def test_feature_matrix_of_an_empty_pool(ctx_and_pool):
    from rqlab.indexes import Ranking

    ctx, _ = ctx_and_pool
    m = feature_matrix("retention", Ranking([], []), ctx)
    assert m.shape[0] == 0


def test_rank_with_weights_returns_a_permutation_of_the_pool(ctx_and_pool):
    ctx, pool = ctx_and_pool
    mat = feature_matrix("retention", pool, ctx)
    out = rank_with_weights(mat, pool.chunk_ids, HAND_WEIGHTS, len(pool.chunk_ids))
    assert sorted(out) == sorted(pool.chunk_ids)


def test_rank_with_weights_respects_k(ctx_and_pool):
    ctx, pool = ctx_and_pool
    mat = feature_matrix("retention", pool, ctx)
    assert len(rank_with_weights(mat, pool.chunk_ids, HAND_WEIGHTS, 5)) == 5


def test_zero_weights_fall_back_to_the_id_tie_break(ctx_and_pool):
    """With no signal every score is identical, so the deterministic tie-break
    on chunk id is what remains. Asserting it pins the reproducibility claim."""
    ctx, pool = ctx_and_pool
    mat = feature_matrix("retention", pool, ctx)
    out = rank_with_weights(mat, pool.chunk_ids, np.zeros(len(FEATURES)),
                            len(pool.chunk_ids))
    assert out == sorted(pool.chunk_ids)


def test_identity_reranker_is_the_pool_order(ctx_and_pool):
    ctx, pool = ctx_and_pool
    assert Identity().rank("retention", pool, ctx, 10) == pool.chunk_ids[:10]


def test_mmr_returns_k_distinct_chunks(ctx_and_pool):
    ctx, pool = ctx_and_pool
    out = MMR().rank("retention enterprise", pool, ctx, 10)
    assert len(out) == 10
    assert len(set(out)) == 10


def test_mmr_at_lambda_one_matches_the_pool_order(ctx_and_pool):
    ctx, pool = ctx_and_pool
    out = MMR(lam=1.0).rank("retention", pool, ctx, 10)
    assert out == pool.chunk_ids[:10]


def test_mmr_at_lower_lambda_diverges_from_the_pool_order(ctx_and_pool):
    ctx, pool = ctx_and_pool
    greedy = MMR(lam=1.0).rank("retention", pool, ctx, 10)
    diverse = MMR(lam=0.1).rank("retention", pool, ctx, 10)
    assert diverse != greedy


def test_linear_features_is_deterministic(ctx_and_pool):
    ctx, pool = ctx_and_pool
    r = LinearFeatures(HAND_WEIGHTS)
    assert r.rank("retention", pool, ctx, 10) == r.rank("retention", pool, ctx, 10)


def test_reranker_of_an_empty_pool(ctx_and_pool):
    from rqlab.indexes import Ranking

    ctx, _ = ctx_and_pool
    empty = Ranking([], [])
    for r in (Identity(), MMR(), LinearFeatures(HAND_WEIGHTS)):
        assert r.rank("retention", empty, ctx, 10) == []


# -- report writer ---------------------------------------------------------

def test_found_without_expect_raises():
    r = Report("t")
    with pytest.raises(AssertionError):
        r.found("a number")


def test_two_expects_without_a_found_raises():
    r = Report("t")
    r.expect("first")
    with pytest.raises(AssertionError):
        r.expect("second")


def test_render_with_an_open_prediction_raises():
    r = Report("t")
    r.expect("something")
    with pytest.raises(AssertionError):
        r.render()


def test_expect_found_pairs_are_counted():
    r = Report("t")
    r.expect("a")
    r.found("b")
    r.expect("c")
    r.found("d", contradicted=True)
    out = r.render()
    assert r.n_predictions == 2
    assert r.n_confirmed == 1
    assert r.n_contradicted == 1
    assert "2 predictions" in out


def test_contradicted_findings_are_labelled():
    r = Report("t")
    r.expect("a")
    r.found("b", contradicted=True)
    assert "prediction wrong" in r.render()


def test_found_splits_paragraphs():
    r = Report("t")
    r.expect("a")
    r.found("first para\n\nsecond para")
    out = r.render()
    assert "**Found.** first para\n\nsecond para" in out


def test_render_is_deterministic():
    def build():
        r = Report("Title")
        r.h2("Section")
        r.para("Some prose that is long enough to require wrapping " * 4)
        r.expect("a prediction")
        r.table(["a", "bb"], [["1", "2"], ["333", "4"]])
        r.found("a measurement")
        return r.render()

    assert build() == build()


def test_table_columns_are_padded_to_the_widest_cell():
    r = Report("t")
    r.table(["a", "bb"], [["longer", "x"]])
    lines = [ln for ln in r.render().splitlines() if ln.startswith("|")]
    assert len({len(ln) for ln in lines}) == 1


def test_wrap_never_exceeds_the_width():
    text = "word " * 200
    for line in _wrap(text).splitlines():
        assert len(line) <= 78


def test_wrap_preserves_every_word():
    text = "alpha beta gamma delta epsilon " * 20
    assert _wrap(text).split() == text.split()


def test_wrap_does_not_break_a_single_long_token():
    long = "x" * 120
    assert long in _wrap(f"short {long} end")


def test_number_formatting():
    assert num(0.5) == "0.500"
    assert num(1.0) == "1.000"
    assert pct(0.1234) == "12.3%"
    assert signed(0.05) == "+0.050"
    assert signed(-0.05) == "-0.050"
    assert signed(0.0) == "+0.000"
