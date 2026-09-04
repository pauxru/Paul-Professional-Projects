"""Metrics, checked against hand-computed values rather than against themselves.

The harm metrics are the ones worth the most attention: they are the reason two
configurations that tie on nDCG can differ by an entire failure mode.
"""

from __future__ import annotations

import math

import pytest

from rqlab.metrics import (
    Judgement,
    answerable_at_k,
    dcg,
    evaluate,
    judge,
    misleading_at_k,
    misleading_first,
    mrr,
    ndcg_at_k,
    recall_at_k,
    unanswered,
)


def J(relevant=(), misleading=(), per_target=None) -> Judgement:
    return Judgement(
        frozenset(relevant),
        frozenset(misleading),
        per_target if per_target is not None else {"t": frozenset(relevant)},
    )


def test_dcg_of_empty_is_zero():
    assert dcg([]) == 0.0


def test_dcg_matches_the_definition():
    assert dcg([1.0, 1.0]) == pytest.approx(1.0 + 1.0 / math.log2(3))


def test_dcg_discounts_later_positions():
    assert dcg([1.0, 0.0]) > dcg([0.0, 1.0])


def test_ndcg_perfect_ranking_is_one():
    j = J(relevant={"a", "b"})
    assert ndcg_at_k(["a", "b", "c"], j, 10) == pytest.approx(1.0)


def test_ndcg_empty_ranking_is_zero():
    assert ndcg_at_k([], J(relevant={"a"}), 10) == 0.0


def test_ndcg_with_no_relevant_chunks_is_zero():
    assert ndcg_at_k(["a", "b"], J(relevant=set(), per_target={"t": frozenset()}), 10) == 0.0


def test_ndcg_hand_computed_for_one_hit_at_rank_two():
    j = J(relevant={"a"})
    assert ndcg_at_k(["x", "a"], j, 10) == pytest.approx(1.0 / math.log2(3))


def test_ndcg_ideal_is_capped_at_k():
    j = J(relevant={"a", "b", "c", "d"})
    # Only two slots, both filled: perfect at k=2 despite two relevant chunks
    # sitting outside the cut.
    assert ndcg_at_k(["a", "b", "z"], j, 2) == pytest.approx(1.0)


def test_ndcg_is_monotone_in_promoting_a_relevant_chunk():
    j = J(relevant={"a"})
    scores = [ndcg_at_k(["x"] * i + ["a"], j, 10) for i in range(5)]
    assert scores == sorted(scores, reverse=True)


def test_recall_is_over_targets_not_over_chunks():
    j = J(per_target={"t1": frozenset({"a", "b", "c"}), "t2": frozenset({"z"})})
    # One chunk of three for t1 is enough; the redundant copies do not matter.
    assert recall_at_k(["a"], j, 10) == pytest.approx(0.5)
    assert recall_at_k(["a", "z"], j, 10) == pytest.approx(1.0)


def test_recall_respects_the_cut():
    j = J(per_target={"t": frozenset({"a"})})
    assert recall_at_k(["x", "y", "a"], j, 2) == 0.0
    assert recall_at_k(["x", "y", "a"], j, 3) == 1.0


def test_recall_with_no_targets_is_zero():
    assert recall_at_k(["a"], J(per_target={}), 10) == 0.0


def test_recall_with_an_unreachable_target_is_below_one():
    j = J(per_target={"t1": frozenset({"a"}), "t2": frozenset()})
    assert recall_at_k(["a"], j, 10) == pytest.approx(0.5)


def test_answerable_requires_every_target():
    j = J(per_target={"t1": frozenset({"a"}), "t2": frozenset({"b"})})
    assert answerable_at_k(["a"], j, 10) == 0.0
    assert answerable_at_k(["a", "b"], j, 10) == 1.0


def test_mrr_reciprocal_rank():
    j = J(relevant={"a"})
    assert mrr(["x", "y", "a"], j) == pytest.approx(1 / 3)
    assert mrr(["a"], j) == pytest.approx(1.0)
    assert mrr(["x"], j) == 0.0


def test_misleading_at_k_counts_presence_not_quantity():
    j = J(relevant={"a"}, misleading={"m1", "m2"})
    assert misleading_at_k(["m1", "m2"], j, 10) == 1.0
    assert misleading_at_k(["a"], j, 10) == 0.0


def test_misleading_first_looks_only_at_the_top():
    j = J(relevant={"a"}, misleading={"m"})
    assert misleading_first(["m", "a"], j) == 1.0
    assert misleading_first(["a", "m"], j) == 0.0
    assert misleading_first([], j) == 0.0


def test_unanswered_is_the_good_failure():
    j = J(relevant={"a"}, misleading={"m"})
    assert unanswered(["x", "y"], j, 10) == 1.0
    assert unanswered(["m"], j, 10) == 0.0
    assert unanswered(["a"], j, 10) == 0.0


def test_unanswered_and_misleading_and_relevant_partition_the_outcomes():
    j = J(relevant={"a"}, misleading={"m"})
    for ranked in ([], ["x"], ["a"], ["m"], ["a", "m"], ["m", "a"]):
        has_rel = bool(set(ranked) & j.relevant)
        has_mis = misleading_at_k(ranked, j, 10) == 1.0
        un = unanswered(ranked, j, 10) == 1.0
        assert un == (not has_rel and not has_mis)


def test_evaluate_returns_every_metric_in_range():
    from rqlab.metrics import METRICS

    j = J(relevant={"a"}, misleading={"m"})
    out = evaluate(["a", "m", "x"], j, k=10)
    assert set(out) == set(METRICS)
    for name, v in out.items():
        assert 0.0 <= v <= 1.0, name


def test_evaluate_respects_a_non_default_k():
    j = J(relevant={"a"})
    out = evaluate(["x", "y", "a"], j, k=2)
    assert "ndcg@2" in out
    assert out["recall@2"] == 0.0


# -- judge() ---------------------------------------------------------------

class _Cov:
    def __init__(self, mapping):
        self.mapping = mapping

    def relevant_chunks(self, fid):
        return self.mapping.get(fid, frozenset())


class _Q:
    def __init__(self, targets, siblings):
        self.targets = targets
        self.siblings = siblings


def test_judge_marks_sibling_answers_as_misleading():
    cov = _Cov({"t": frozenset({"c1"}), "s": frozenset({"c2"})})
    j = judge(_Q(("t",), ("s",)), cov, {})
    assert j.relevant == frozenset({"c1"})
    assert j.misleading == frozenset({"c2"})


def test_judge_marks_partial_chunks_of_the_target_as_misleading():
    cov = _Cov({"t": frozenset({"c1"})})
    j = judge(_Q(("t",), ()), cov, {"t": frozenset({"c9"})})
    assert j.misleading == frozenset({"c9"})


def test_judge_never_calls_a_relevant_chunk_misleading():
    """A table row states every plan's value beside the label distinguishing
    them, so the same chunk covers a target and a sibling. It answers the
    question; it is not misleading."""
    cov = _Cov({"t": frozenset({"c1"}), "s": frozenset({"c1"})})
    j = judge(_Q(("t",), ("s",)), cov, {})
    assert j.relevant == frozenset({"c1"})
    assert j.misleading == frozenset()


def test_judge_records_per_target_even_when_unreachable():
    cov = _Cov({})
    j = judge(_Q(("t",), ()), cov, {})
    assert j.per_target == {"t": frozenset()}
    assert j.relevant == frozenset()
