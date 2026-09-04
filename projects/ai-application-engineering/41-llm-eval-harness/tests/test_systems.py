"""Tests for the simulated systems.

The simulation is the load-bearing part of this repository. Every number in
the report comes out of it, so a modelling mistake here does not produce a
wrong test result -- it produces a *plausible* report that argues for the
wrong thing. Two of the four bugs found while building this project were
exactly that, and both are pinned below.
"""
from __future__ import annotations

import numpy as np
import pytest

from evalharness.corpus import make_dataset
from evalharness.dataset import Dataset, Item
from evalharness.systems import (Response, SimulatedJudge, SimulatedModel,
                                 _item_seed, run)


def ds(n: int = 60, name: str = "t", version: str = "v1") -> Dataset:
    return make_dataset(name, version, n)


# --------------------------------------------------------------------------
# Determinism
# --------------------------------------------------------------------------

def test_same_model_same_item_gives_same_response():
    m = SimulatedModel("m", seed=3)
    item = ds().items[0]
    assert m.run(item) == m.run(item)


def test_two_models_same_name_and_seed_agree():
    item = ds().items[0]
    assert SimulatedModel("m", seed=3).run(item) == SimulatedModel("m", seed=3).run(item)


def test_different_seed_gives_different_response():
    item = ds().items[0]
    assert SimulatedModel("m", seed=3).run(item) != SimulatedModel("m", seed=4).run(item)


def test_run_is_reproducible_across_calls():
    d = ds()
    a = run(SimulatedModel("m", seed=1), SimulatedJudge("j", seed=2), d)
    b = run(SimulatedModel("m", seed=1), SimulatedJudge("j", seed=2), d)
    assert np.array_equal(a.scores, b.scores)
    assert np.array_equal(a.truth, b.truth)
    assert a.item_ids == b.item_ids


# --------------------------------------------------------------------------
# _item_seed: stability under dataset growth
# --------------------------------------------------------------------------

def test_item_seed_is_stable_regardless_of_position():
    """An item's seed must not depend on the dataset it happens to sit in.

    If it did, adding one item to the eval set would silently re-roll every
    other item's response, and every historical comparison would become
    meaningless without anything visibly changing.
    """
    assert _item_seed("abc", "salt") == _item_seed("abc", "salt")


def test_item_seed_differs_by_item():
    assert _item_seed("abc", "s") != _item_seed("abd", "s")


def test_item_seed_differs_by_salt():
    assert _item_seed("abc", "s1") != _item_seed("abc", "s2")


def test_item_seed_in_valid_range():
    for i in range(200):
        s = _item_seed(f"item-{i}", "salt")
        assert 0 <= s < 2 ** 64


def test_growing_the_dataset_does_not_change_existing_scores():
    small = make_dataset("corpus", "v1", 40)
    m, j = SimulatedModel("m", seed=1), SimulatedJudge("j", seed=2)
    r_small = run(m, j, small)

    extra = list(small.items) + [Item(id="corpus:extra-000", prompt="p",
                                     reference="r", tier="easy")]
    grown = Dataset(name="corpus", version="v2", items=tuple(extra))
    r_grown = run(m, j, grown)

    by_id = dict(zip(r_grown.item_ids, r_grown.scores))
    for iid, score in zip(r_small.item_ids, r_small.scores):
        assert by_id[iid] == pytest.approx(score)


# --------------------------------------------------------------------------
# shared_variance: the decomposition the paired statistics depend on
# --------------------------------------------------------------------------

def _paired_sd(shared: float, n: int = 400) -> float:
    d = make_dataset("c", "v1", n)
    j = SimulatedJudge("j", noise=0.0, seed=9)
    a = SimulatedModel("a", shared_variance=shared, seed=1)
    b = a.variant("b", quality_delta=0.03)
    ra, rb = run(a, j, d), run(b, j, d)
    return float(np.std(rb.scores - ra.scores, ddof=1))


def test_shared_variance_reduces_paired_spread():
    """This is why paired designs work, and it is a property of the systems.

    Two systems that share most of their difficulty structure differ by much
    less, item to item, than their individual spreads suggest. If the
    simulation did not reproduce this, every power calculation in the report
    would be describing a world that does not exist.
    """
    assert _paired_sd(0.9) < _paired_sd(0.5) < _paired_sd(0.0)


def test_shared_variance_does_not_change_marginal_spread_much():
    """The decomposition must move covariance without moving the marginals."""
    d = make_dataset("c", "v1", 400)
    j = SimulatedJudge("j", noise=0.0, seed=9)
    lo = run(SimulatedModel("a", shared_variance=0.0, seed=1), j, d)
    hi = run(SimulatedModel("a", shared_variance=0.95, seed=1), j, d)
    assert np.std(lo.scores) == pytest.approx(np.std(hi.scores), abs=0.04)


def test_shared_variance_bounds_are_enforced():
    with pytest.raises(ValueError):
        SimulatedModel("m", shared_variance=1.4)
    with pytest.raises(ValueError):
        SimulatedModel("m", shared_variance=-0.1)


# --------------------------------------------------------------------------
# Judge consistency: bug 13
# --------------------------------------------------------------------------

def test_judge_noise_does_not_cancel_completely_in_paired_differences():
    """Regression test for the bug that made judge noise look free.

    The first version keyed judge noise on the item id alone. In a paired
    comparison both systems are scored on the same items, so both got the
    identical perturbation and it subtracted out exactly. Adding noise to the
    judge then had no effect at all on the width of the paired interval, which
    is the opposite of the truth and would have made the report argue that
    judge quality does not matter for A/B decisions.
    """
    d = make_dataset("c", "v1", 300)
    a = SimulatedModel("a", seed=1)
    b = a.variant("b", quality_delta=0.03)

    sds = [float(np.std(run(b, SimulatedJudge("j", noise=nz, consistency=0.5, seed=4), d).scores
                        - run(a, SimulatedJudge("j", noise=nz, consistency=0.5, seed=4), d).scores))
           for nz in (0.001, 0.05, 0.20)]
    assert sds[0] < sds[1] < sds[2]
    assert sds[2] > sds[0] * 1.3


def test_fully_consistent_judge_error_does_cancel():
    """The stable component *should* cancel -- that is what makes it stable."""
    d = make_dataset("c", "v1", 300)
    a = SimulatedModel("a", seed=1)
    b = a.variant("b", quality_delta=0.03)

    stable = SimulatedJudge("j", noise=0.20, consistency=1.0, seed=4)
    flaky = SimulatedJudge("j", noise=0.20, consistency=0.0, seed=4)

    sd_stable = np.std(run(b, stable, d).scores - run(a, stable, d).scores)
    sd_flaky = np.std(run(b, flaky, d).scores - run(a, flaky, d).scores)
    assert sd_stable < sd_flaky


def test_judge_consistency_bounds_are_enforced():
    with pytest.raises(ValueError):
        SimulatedJudge("j", consistency=1.1)
    with pytest.raises(ValueError):
        SimulatedJudge("j", consistency=-0.01)


def test_zero_noise_judge_reports_quality_exactly():
    d = ds(30)
    j = SimulatedJudge("j", noise=0.0, consistency=0.5, seed=1)
    m = SimulatedModel("m", seed=1)
    for item in d.items:
        assert j.score(item, m.run(item)) == pytest.approx(m.run(item).quality, abs=1e-9)


def test_judge_bias_shifts_scores_up():
    d = ds(200)
    m = SimulatedModel("m", seed=1)
    plain = run(m, SimulatedJudge("j", noise=0.02, bias=0.0, seed=1), d)
    kind = run(m, SimulatedJudge("j", noise=0.02, bias=0.10, seed=1), d)
    assert kind.mean_score > plain.mean_score + 0.05


def test_judge_scores_stay_in_unit_interval():
    d = ds(200)
    r = run(SimulatedModel("m", seed=1),
            SimulatedJudge("j", noise=0.5, bias=0.4, seed=1), d)
    assert r.scores.min() >= 0.0
    assert r.scores.max() <= 1.0


def test_length_bias_rewards_longer_output():
    item = ds(5).items[0]
    j = SimulatedJudge("j", noise=0.0, length_bias=0.5, seed=1)
    short = Response(item.id, "x" * 50, 0.5, 100.0, 10, 50)
    long = Response(item.id, "x" * 4000, 0.5, 100.0, 10, 4000)
    assert j.score(item, long) > j.score(item, short)


def test_no_length_bias_ignores_output_length():
    item = ds(5).items[0]
    j = SimulatedJudge("j", noise=0.0, length_bias=0.0, seed=1)
    short = Response(item.id, "x" * 50, 0.5, 100.0, 10, 50)
    long = Response(item.id, "x" * 4000, 0.5, 100.0, 10, 4000)
    assert j.score(item, short) == pytest.approx(j.score(item, long))


# --------------------------------------------------------------------------
# variant()
# --------------------------------------------------------------------------

def test_variant_shifts_mean_quality():
    d = ds(400)
    j = SimulatedJudge("j", noise=0.0, seed=1)
    base = SimulatedModel("base", seed=1)
    better = base.variant("better", quality_delta=0.05)
    delta = run(better, j, d).mean_score - run(base, j, d).mean_score
    assert delta == pytest.approx(0.05, abs=0.02)


def test_variant_preserves_shared_structure():
    """A variant must correlate with its parent, or pairing buys nothing."""
    d = ds(300)
    j = SimulatedJudge("j", noise=0.0, seed=1)
    base = SimulatedModel("base", seed=1)
    better = base.variant("better", quality_delta=0.02)
    r = np.corrcoef(run(base, j, d).scores, run(better, j, d).scores)[0, 1]
    assert r > 0.8


def test_variant_keeps_its_own_name():
    assert SimulatedModel("a", seed=1).variant("b").name == "b"


def test_variant_can_override_other_fields():
    v = SimulatedModel("a", seed=1, latency_ms=900).variant("b", latency_ms=1500)
    assert v.latency_ms == 1500
    assert v.name == "b"


def test_variant_of_variant_is_cumulative():
    d = ds(400)
    j = SimulatedJudge("j", noise=0.0, seed=1)
    a = SimulatedModel("a", seed=1)
    c = a.variant("b", quality_delta=0.03).variant("c", quality_delta=0.03)
    assert run(c, j, d).mean_score - run(a, j, d).mean_score == pytest.approx(0.06, abs=0.02)


# --------------------------------------------------------------------------
# RunResult
# --------------------------------------------------------------------------

def test_run_result_carries_dataset_identity():
    d = ds(20, name="corpus", version="v7")
    r = run(SimulatedModel("m", seed=1), SimulatedJudge("j", seed=1), d)
    assert r.dataset_name == "corpus"
    assert r.dataset_version == "v7"
    assert r.dataset_fingerprint == d.fingerprint


def test_run_result_item_ids_match_dataset_order():
    d = ds(20)
    r = run(SimulatedModel("m", seed=1), SimulatedJudge("j", seed=1), d)
    assert r.item_ids == tuple(i.id for i in d.items)


def test_p95_latency_is_at_least_p50():
    d = ds(200)
    r = run(SimulatedModel("m", seed=1), SimulatedJudge("j", seed=1), d)
    assert r.p95_latency >= r.p50_latency


def test_latencies_are_positive():
    d = ds(200)
    r = run(SimulatedModel("m", latency_ms=300, latency_spread=500, seed=1),
            SimulatedJudge("j", seed=1), d)
    assert r.latencies_ms.min() > 0


def test_cost_scales_with_token_price():
    d = ds(50)
    r = run(SimulatedModel("m", seed=1), SimulatedJudge("j", seed=1), d)
    assert r.cost_usd(0.006, 0.030) == pytest.approx(2 * r.cost_usd(0.003, 0.015))


def test_cost_is_positive():
    d = ds(50)
    r = run(SimulatedModel("m", seed=1), SimulatedJudge("j", seed=1), d)
    assert r.cost_usd() > 0


def test_slice_scores_partitions_the_dataset():
    d = make_dataset("c", "v1", 120,
                     composition={"easy": 0.5, "hard": 0.5})
    r = run(SimulatedModel("m", seed=1), SimulatedJudge("j", seed=1), d)
    total = sum(len(r.slice_scores(d, t)) for t in ("easy", "hard"))
    assert total == 120


def test_slice_scores_unknown_tier_is_rejected():
    """A misspelled slice name must fail loudly, not return an empty slice.

    Returning [] would make `compare()` silently skip the slice, and a gate
    that skips a slice reports no regression in it.
    """
    d = ds(40)
    r = run(SimulatedModel("m", seed=1), SimulatedJudge("j", seed=1), d)
    with pytest.raises(ValueError):
        r.slice_scores(d, "nonexistent")


def test_tier_offset_is_signed_not_a_penalty():
    """Negative lowers, positive raises. The name has to survive being read.

    This assertion is what caught the inverted field described in
    SimulatedModel's docstring: the parameter was called `tier_penalty` and
    added, so a caller who supplied a positive "penalty" made the model
    better on those items.
    """
    d = make_dataset("c", "v1", 300,
                     composition={"easy": 0.5, "hard": 0.5})
    worse = SimulatedModel("m", seed=1, tier_offset={"hard": -0.25})
    r = run(worse, SimulatedJudge("j", noise=0.0, seed=1), d)
    assert r.slice_scores(d, "hard").mean() < r.slice_scores(d, "easy").mean() - 0.1

    better = SimulatedModel("m", seed=1, tier_offset={"hard": +0.20})
    r2 = run(better, SimulatedJudge("j", noise=0.0, seed=1), d)
    assert r2.slice_scores(d, "hard").mean() > r2.slice_scores(d, "easy").mean()


def test_mean_truth_tracks_mean_score_for_a_clean_judge():
    d = ds(300)
    r = run(SimulatedModel("m", seed=1), SimulatedJudge("j", noise=0.0, seed=1), d)
    assert r.mean_score == pytest.approx(r.mean_truth, abs=1e-9)


def test_truth_is_unaffected_by_judge():
    d = ds(100)
    m = SimulatedModel("m", seed=1)
    a = run(m, SimulatedJudge("j1", noise=0.30, bias=0.2, seed=1), d)
    b = run(m, SimulatedJudge("j2", noise=0.01, bias=0.0, seed=9), d)
    assert np.array_equal(a.truth, b.truth)


def test_empty_dataset_is_rejected():
    with pytest.raises(ValueError):
        run(SimulatedModel("m", seed=1), SimulatedJudge("j", seed=1),
            Dataset(name="e", version="v1", items=()))


def test_response_cost_uses_both_token_counts():
    r = Response("i", "t", 0.5, 100.0, 1000, 1000)
    assert r.cost_usd(0.003, 0.015) == pytest.approx(0.018)
