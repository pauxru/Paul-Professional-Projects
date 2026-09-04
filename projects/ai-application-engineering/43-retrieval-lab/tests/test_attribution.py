"""Attribution: the decomposition that has to sum to one.

If these fail, every "which stage is at fault" claim in the report is void.
"""

from __future__ import annotations

import pytest

from rqlab.attribution import (
    STAGES,
    Attribution,
    attribute_query,
    check_exhaustive,
    ladder_from,
)
from rqlab.metrics import Judgement


def Q(targets):
    class _Q:
        def __init__(self):
            self.targets = targets
            self.siblings = ()
    return _Q()


def Jd(per_target):
    return Judgement(frozenset(), frozenset(), per_target)


def test_empty_attribution_shares_are_zero():
    a = Attribution()
    assert a.total == 0
    assert set(a.share()) == set(STAGES)
    assert all(v == 0.0 for v in a.share().values())


def test_shares_sum_to_one_when_nonempty():
    a = Attribution()
    a.counts["served"] = 3
    a.counts["chunker"] = 1
    assert sum(a.share().values()) == pytest.approx(1.0)


def test_add_accumulates_every_stage():
    a, b = Attribution(), Attribution()
    for s in STAGES:
        a.counts[s] = 1
        b.counts[s] = 2
    a.add(b)
    assert all(a.counts[s] == 3 for s in STAGES)


def test_unreachable_target_is_the_chunkers_fault():
    a = attribute_query(Q(("t",)), Jd({"t": frozenset()}), [], [], 10)
    assert a.counts["chunker"] == 1


def test_reachable_but_not_pooled_is_the_retrievers_fault():
    a = attribute_query(Q(("t",)), Jd({"t": frozenset({"c"})}), ["x"], ["x"], 10)
    assert a.counts["retriever"] == 1


def test_pooled_but_below_the_cut_is_the_rankers_fault():
    j = Jd({"t": frozenset({"c"})})
    a = attribute_query(Q(("t",)), j, ["c", "x"], ["x", "c"], 1)
    assert a.counts["ranker"] == 1


def test_served_when_inside_the_cut():
    j = Jd({"t": frozenset({"c"})})
    a = attribute_query(Q(("t",)), j, ["c"], ["c"], 1)
    assert a.counts["served"] == 1


def test_every_target_lands_in_exactly_one_stage():
    j = Jd({
        "a": frozenset(),
        "b": frozenset({"c1"}),
        "c": frozenset({"c2"}),
        "d": frozenset({"c3"}),
    })
    a = attribute_query(Q(("a", "b", "c", "d")), j, ["c2", "c3"], ["c3", "c2"], 1)
    assert a.total == 4
    assert a.counts == {"served": 1, "chunker": 1, "retriever": 1, "ranker": 1}


def test_check_exhaustive_raises_on_a_mismatch():
    a = Attribution()
    a.counts["served"] = 2
    with pytest.raises(AssertionError):
        check_exhaustive(a, 3)


def test_check_exhaustive_passes_on_a_match():
    a = Attribution()
    a.counts["served"] = 3
    check_exhaustive(a, 3)


def test_ladder_is_a_decreasing_sequence():
    a = Attribution()
    a.counts.update(served=5, ranker=2, retriever=2, chunker=1)
    lad = ladder_from(a)
    assert lad.reachable >= lad.pooled >= lad.served
    assert lad.reachable == pytest.approx(9 / 10)
    assert lad.pooled == pytest.approx(7 / 10)
    assert lad.served == pytest.approx(5 / 10)


def test_ladder_of_an_empty_attribution_is_all_zero():
    lad = ladder_from(Attribution())
    assert lad.as_row() == (0.0, 0.0, 0.0)


def test_binding_stage_names_the_largest_loss():
    a = Attribution()
    a.counts.update(served=1, chunker=5, retriever=1, ranker=1)
    assert ladder_from(a).binding_stage() == "chunker"
    b = Attribution()
    b.counts.update(served=1, chunker=1, retriever=5, ranker=1)
    assert ladder_from(b).binding_stage() == "retriever"


def test_binding_stage_ignores_served():
    a = Attribution()
    a.counts.update(served=100, chunker=0, retriever=1, ranker=0)
    assert ladder_from(a).binding_stage() == "retriever"
