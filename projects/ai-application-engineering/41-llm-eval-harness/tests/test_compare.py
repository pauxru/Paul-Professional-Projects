"""Tests for the release gate.

`compare()` is the only part of this repository that would ever be wired into
CI, which makes it the only part where a bug blocks a good change or ships a
bad one. The decision cascade is tested case by case, including the orderings
where two rules could both fire and only one is correct.
"""
from __future__ import annotations

import numpy as np
import pytest

from evalharness.compare import SliceResult, Verdict, _decide, compare
from evalharness.stats import Interval
from evalharness.corpus import make_dataset
from evalharness.dataset import Dataset
from evalharness.systems import SimulatedJudge, SimulatedModel, run

RESAMPLES = 800


def setup(n=200, delta=0.0, seed=1, judge_noise=0.02, **model_kw):
    d = make_dataset("corpus", "v1", n)
    j = SimulatedJudge("j", noise=judge_noise, seed=seed)
    a = SimulatedModel("baseline", seed=seed, **model_kw)
    b = a.variant("candidate", quality_delta=delta)
    return d, run(a, j, d), run(b, j, d)


def cmp(d, ra, rb, **kw):
    kw.setdefault("resamples", RESAMPLES)
    kw.setdefault("seed", 7)
    return compare(ra, rb, d, **kw)


# --------------------------------------------------------------------------
# Comparability guards -- these run before any statistics
# --------------------------------------------------------------------------

def test_different_item_ids_are_incomparable():
    d1, ra, _ = setup(n=80)
    d2 = make_dataset("other", "v1", 80)
    rb = run(SimulatedModel("candidate", seed=1), SimulatedJudge("j", seed=1), d2)
    c = compare(ra, rb, d1, resamples=RESAMPLES)
    assert c.verdict is Verdict.INCOMPARABLE
    assert "paired" in c.reason


def test_changed_dataset_version_is_incomparable():
    """The fingerprint check is the one that catches the expensive mistake.

    Editing three items in the eval set and rerunning produces two runs that
    look perfectly comparable -- same count, same ids if the edits were to
    prompts rather than ids -- and a difference that is partly the change and
    partly the new questions. Nothing else in the pipeline notices.
    """
    d1 = make_dataset("corpus", "v1", 80)
    items = list(d1.items)
    items[0] = type(items[0])(id=items[0].id, prompt="EDITED",
                              reference=items[0].reference, tier=items[0].tier,
                              tags=items[0].tags)
    d2 = Dataset(name="corpus", version="v2", items=tuple(items))

    j = SimulatedJudge("j", seed=1)
    ra = run(SimulatedModel("baseline", seed=1), j, d1)
    rb = run(SimulatedModel("candidate", seed=1), j, d2)
    c = compare(ra, rb, d1, resamples=RESAMPLES)
    assert c.verdict is Verdict.INCOMPARABLE
    assert "fingerprint" in c.reason


def test_dataset_mismatch_can_be_explicitly_allowed():
    d1 = make_dataset("corpus", "v1", 80)
    items = list(d1.items)
    items[0] = type(items[0])(id=items[0].id, prompt="EDITED",
                              reference=items[0].reference, tier=items[0].tier,
                              tags=items[0].tags)
    d2 = Dataset(name="corpus", version="v2", items=tuple(items))
    j = SimulatedJudge("j", seed=1)
    ra = run(SimulatedModel("baseline", seed=1), j, d1)
    rb = run(SimulatedModel("candidate", seed=1), j, d2)
    c = compare(ra, rb, d1, resamples=RESAMPLES, allow_dataset_mismatch=True)
    assert c.verdict is not Verdict.INCOMPARABLE


def test_incomparable_reports_n_zero_and_blocks():
    d1, ra, _ = setup(n=80)
    d2 = make_dataset("other", "v1", 80)
    rb = run(SimulatedModel("c", seed=1), SimulatedJudge("j", seed=1), d2)
    c = compare(ra, rb, d1, resamples=RESAMPLES)
    assert c.n == 0
    assert c.blocked


def test_incomparable_mde_is_nan_not_zero():
    """NaN, because the MDE is undefined here -- not zero, which reads as
    'this eval set can detect any effect at all'."""
    d1, ra, _ = setup(n=80)
    d2 = make_dataset("other", "v1", 80)
    rb = run(SimulatedModel("c", seed=1), SimulatedJudge("j", seed=1), d2)
    c = compare(ra, rb, d1, resamples=RESAMPLES)
    assert np.isnan(c.mde)


# --------------------------------------------------------------------------
# The verdict cascade
# --------------------------------------------------------------------------

def test_large_improvement_is_improved():
    d, ra, rb = setup(n=400, delta=0.10)
    c = cmp(d, ra, rb)
    assert c.verdict is Verdict.IMPROVED
    assert not c.blocked


def test_large_regression_is_regressed():
    d, ra, rb = setup(n=400, delta=-0.10)
    c = cmp(d, ra, rb)
    assert c.verdict is Verdict.REGRESSED
    assert c.blocked


def test_identical_runs_are_never_improved():
    d, ra, _ = setup(n=200)
    c = cmp(d, ra, ra)
    assert c.verdict in (Verdict.UNDERPOWERED, Verdict.INDISTINGUISHABLE)
    assert not c.blocked


def test_identical_runs_report_zero_delta():
    d, ra, _ = setup(n=200)
    assert cmp(d, ra, ra).overall.delta == pytest.approx(0.0)


def test_tiny_effect_on_small_n_is_usually_underpowered():
    """The distinction the whole harness exists for.

    A 30-item eval set that reports "no significant change" for a +0.01 shift
    has not established that nothing happened. Asserted across seeds rather
    than on one draw, because on one draw it is not reliably true -- see
    test_a_fixed_small_eval_set_bakes_in_its_own_error for why.
    """
    verdicts = []
    for seed in range(10):
        d, ra, rb = setup(n=30, delta=0.01, seed=seed)
        verdicts.append(cmp(d, ra, rb, seed=seed).verdict)
    assert verdicts.count(Verdict.UNDERPOWERED) >= 5
    assert Verdict.REGRESSED not in verdicts


def test_a_fixed_small_eval_set_bakes_in_its_own_error():
    """Small-eval-set error is not noise you can average away.

    Sampling error in a *fixed* eval set is not resampled on every run. If the
    30 items you chose happen to favour the candidate by +0.09 when the true
    effect is +0.01, they will favour it by +0.09 every single time you run
    them, for as long as that file is your eval set. Rerunning is not a
    remedy; it reproduces the error exactly.

    Measured here as the spread of the error across model seeds, holding the
    item subset fixed -- which is the same shape as holding the model fixed
    and varying the subset, and is the quantity nobody reports.
    """
    d_big = make_dataset("corpus", "v1", 2000)
    d_small = make_dataset("corpus", "v1", 30)
    j = SimulatedJudge("j", noise=0.02, seed=1)

    errors = []
    for seed in range(10):
        a = SimulatedModel("baseline", seed=seed)
        b = a.variant("candidate", quality_delta=0.01)
        ra, rb = run(a, j, d_big), run(b, j, d_big)
        per_item = dict(zip(d_big.ids(), rb.scores - ra.scores))
        subset = np.array([per_item[i] for i in d_small.ids()])
        errors.append(float(subset.mean() - (rb.scores - ra.scores).mean()))

    errors = np.array(errors)
    assert errors.std() > 0.01
    assert errors.max() > 0.03
    assert errors.min() < 0.0


def test_underpowered_reason_quotes_the_mde():
    for seed in range(10):
        d, ra, rb = setup(n=30, delta=0.0, seed=seed)
        c = cmp(d, ra, rb, seed=seed)
        if c.verdict is Verdict.UNDERPOWERED:
            assert f"{c.mde:.4f}" in c.reason
            return
    pytest.fail("no underpowered case found in 10 seeds")


def test_mde_shrinks_as_n_grows():
    _, ra1, rb1 = setup(n=50)
    _, ra2, rb2 = setup(n=800)
    d1 = make_dataset("corpus", "v1", 50)
    d2 = make_dataset("corpus", "v1", 800)
    assert cmp(d2, ra2, rb2).mde < cmp(d1, ra1, rb1).mde


# --------------------------------------------------------------------------
# Tolerance
# --------------------------------------------------------------------------

def test_tolerance_lets_a_small_regression_through():
    d, ra, rb = setup(n=400, delta=-0.03)
    strict = cmp(d, ra, rb, regression_tolerance=0.0)
    lax = cmp(d, ra, rb, regression_tolerance=0.20)
    assert strict.verdict is Verdict.REGRESSED
    assert lax.verdict is not Verdict.REGRESSED


def test_tolerance_does_not_rescue_a_large_regression():
    d, ra, rb = setup(n=400, delta=-0.30)
    assert cmp(d, ra, rb, regression_tolerance=0.05).verdict is Verdict.REGRESSED


def test_tolerance_appears_in_the_reason():
    assert "tolerance" in _decide(sr(-0.10, -0.14, -0.06), (), 0.02, 0.01)[1]


# --------------------------------------------------------------------------
# The cascade, tested directly
#
# End-to-end the rules mask each other -- a uniform regression trips the slice
# rule before the aggregate rule is ever reached -- so precedence is pinned
# here on synthetic inputs where each branch is reachable in isolation.
# --------------------------------------------------------------------------

def sr(delta, low, high, *, name="overall", n=200, p=0.001, significant=False):
    return SliceResult(name, n, 0.75, 0.75 + delta,
                       Interval(delta, low, high, 0.95, "bca"), p,
                       significant=significant)


def test_cascade_slice_harm_outranks_a_flat_aggregate():
    v, why = _decide(sr(0.0, -0.01, 0.01),
                     (sr(-0.08, -0.12, -0.04, name="adversarial", n=60,
                         significant=True),), 0.02, 0.0)
    assert v is Verdict.REGRESSED
    assert "adversarial" in why


def test_cascade_slice_harm_outranks_a_significant_overall_gain():
    """A change that lifts the average and breaks one slice is still a
    regression. Shipping it because the mean improved is the specific decision
    this ordering exists to prevent."""
    v, _ = _decide(sr(0.05, 0.02, 0.08),
                   (sr(-0.09, -0.14, -0.05, name="hard", n=60,
                       significant=True),), 0.02, 0.0)
    assert v is Verdict.REGRESSED


def test_cascade_ignores_a_slice_that_did_not_survive_correction():
    """`significant` is the post-BH flag. An uncorrected slice p-value must
    not block, or four slices give an 18.5% chance of blocking a good change."""
    v, _ = _decide(sr(0.0, -0.01, 0.01),
                   (sr(-0.08, -0.12, -0.04, name="hard", n=60,
                       significant=False),), 0.02, 0.0)
    assert v is not Verdict.REGRESSED


def test_cascade_ignores_a_slice_whose_interval_touches_zero():
    v, _ = _decide(sr(0.0, -0.01, 0.01),
                   (sr(-0.08, -0.16, 0.01, name="hard", n=60,
                       significant=True),), 0.02, 0.0)
    assert v is not Verdict.REGRESSED


def test_cascade_overall_harm_blocks_when_no_slice_does():
    v, why = _decide(sr(-0.10, -0.14, -0.06), (), 0.02, 0.0)
    assert v is Verdict.REGRESSED
    assert "tolerance" in why


def test_cascade_overall_gain_is_improved():
    v, why = _decide(sr(0.06, 0.02, 0.10), (), 0.02, 0.0)
    assert v is Verdict.IMPROVED
    assert "excludes zero" in why


def test_cascade_small_straddling_effect_is_underpowered():
    v, why = _decide(sr(0.01, -0.03, 0.05), (), 0.04, 0.0)
    assert v is Verdict.UNDERPOWERED
    assert "absence of evidence" in why


def test_cascade_large_straddling_effect_is_indistinguishable_not_underpowered():
    """Above the MDE the eval set *could* have seen it and did not. That is a
    real null result and must not be reported as an inconclusive one."""
    v, why = _decide(sr(0.09, -0.02, 0.20), (), 0.04, 0.0)
    assert v is Verdict.INDISTINGUISHABLE
    assert "straddles zero" in why


def test_cascade_boundary_delta_exactly_at_mde_is_indistinguishable():
    assert _decide(sr(0.04, -0.02, 0.10), (), 0.04, 0.0)[0] is Verdict.INDISTINGUISHABLE


def test_cascade_interval_low_exactly_zero_is_not_improved():
    """`low > 0`, not `>= 0`: an interval whose lower bound is exactly zero is
    consistent with no effect at all."""
    assert _decide(sr(0.06, 0.0, 0.12), (), 0.02, 0.0)[0] is not Verdict.IMPROVED


def test_cascade_tolerance_shifts_the_regression_boundary():
    slim = sr(-0.03, -0.05, -0.02)
    assert _decide(slim, (), 0.01, 0.0)[0] is Verdict.REGRESSED
    assert _decide(slim, (), 0.01, 0.05)[0] is not Verdict.REGRESSED


# --------------------------------------------------------------------------
# Slices: the case the aggregate is blind to
# --------------------------------------------------------------------------

def test_slice_regression_beats_a_flat_aggregate():
    """A change that helps easy items and breaks adversarial ones.

    The means cancel. The aggregate says nothing happened. This is the single
    most important behaviour in the file, because it is the failure mode that
    a mean-of-means dashboard cannot express at all.
    """
    d = make_dataset("corpus", "v1", 600)
    j = SimulatedJudge("j", noise=0.02, seed=3)
    base = SimulatedModel("baseline", seed=3)
    skew = base.variant("candidate")
    skew.tier_offset = dict(base.tier_offset)
    skew.tier_offset["easy"] += 0.10
    skew.tier_offset["adversarial"] -= 0.28

    c = compare(run(base, j, d), run(skew, j, d), d, resamples=2000, seed=5)
    assert c.verdict is Verdict.REGRESSED
    assert "adversarial" in c.reason
    assert "hides" in c.reason


def test_slices_below_five_items_are_skipped():
    """Two-item slices produce intervals wide enough to mean nothing, and
    printing one invites somebody to read the point estimate off it."""
    d = make_dataset("corpus", "v1", 100,
                     composition={"easy": 0.96, "medium": 0.02,
                                  "hard": 0.01, "adversarial": 0.01})
    j = SimulatedJudge("j", seed=1)
    a = SimulatedModel("baseline", seed=1)
    c = compare(run(a, j, d), run(a.variant("cand"), j, d), d, resamples=RESAMPLES)
    for s in c.slices:
        assert s.n >= 5


def test_every_reported_slice_is_a_real_tier():
    d, ra, rb = setup(n=400)
    names = {s.name for s in cmp(d, ra, rb).slices}
    assert names <= {"easy", "medium", "hard", "adversarial"}


def test_slice_ns_sum_to_at_most_total():
    d, ra, rb = setup(n=400)
    c = cmp(d, ra, rb)
    assert sum(s.n for s in c.slices) <= c.n


def test_slice_deltas_are_candidate_minus_baseline():
    d, ra, rb = setup(n=400, delta=0.08)
    for s in cmp(d, ra, rb).slices:
        assert s.delta == pytest.approx(s.candidate_mean - s.baseline_mean)


def test_null_change_rarely_flags_a_slice():
    """With BH correction across four slices, a no-op change should almost
    never produce a significant slice. This is the false-positive side of the
    gate and the reason the correction is there at all."""
    flagged = 0
    for seed in range(12):
        d = make_dataset("corpus", "v1", 300)
        j = SimulatedJudge("j", noise=0.05, seed=seed)
        a = SimulatedModel("baseline", seed=seed)
        c = compare(run(a, j, d), run(a.variant("cand"), j, d), d,
                    resamples=1000, seed=seed)
        flagged += sum(1 for s in c.slices if s.significant)
    assert flagged <= 3


# --------------------------------------------------------------------------
# Reported metadata
# --------------------------------------------------------------------------

def test_cost_delta_is_reported():
    d, ra, rb = setup(n=200)
    assert isinstance(cmp(d, ra, rb).cost_delta_usd, float)


def test_a_cheaper_candidate_shows_negative_cost_delta():
    d = make_dataset("corpus", "v1", 200)
    j = SimulatedJudge("j", seed=1)
    a = SimulatedModel("baseline", seed=1, tokens_out=400)
    b = a.variant("candidate", tokens_out=100)
    assert compare(run(a, j, d), run(b, j, d), d, resamples=RESAMPLES).cost_delta_usd < 0


def test_a_slower_candidate_shows_positive_latency_delta():
    d = make_dataset("corpus", "v1", 200)
    j = SimulatedJudge("j", seed=1)
    a = SimulatedModel("baseline", seed=1, latency_ms=500)
    b = a.variant("candidate", latency_ms=1500)
    assert compare(run(a, j, d), run(b, j, d), d,
                   resamples=RESAMPLES).p95_latency_delta_ms > 500


def test_summary_mentions_the_verdict():
    d, ra, rb = setup(n=300, delta=0.10)
    c = cmp(d, ra, rb)
    assert c.verdict.value.lower() in c.summary().lower()


def test_summary_is_not_empty_for_every_verdict():
    for n, delta in ((400, 0.12), (400, -0.12), (30, 0.005), (300, 0.0)):
        d = make_dataset("corpus", "v1", n)
        _, ra, rb = setup(n=n, delta=delta)
        assert len(cmp(d, ra, rb).summary()) > 20


def test_only_regressed_and_incomparable_block():
    """A gate that blocks on UNDERPOWERED stops all work; a gate that treats
    INCOMPARABLE as a pass ships on a broken measurement."""
    blocking = {Verdict.REGRESSED, Verdict.INCOMPARABLE}
    for v in Verdict:
        d, ra, rb = setup(n=200)
        c = cmp(d, ra, rb)
        object.__setattr__(c, "verdict", v)
        assert c.blocked == (v in blocking), v


# --------------------------------------------------------------------------
# Determinism
# --------------------------------------------------------------------------

def test_compare_is_deterministic_for_a_fixed_seed():
    d, ra, rb = setup(n=200, delta=0.05)
    c1, c2 = cmp(d, ra, rb), cmp(d, ra, rb)
    assert c1.verdict is c2.verdict
    assert c1.overall.interval.low == c2.overall.interval.low
    assert c1.overall.p_value == c2.overall.p_value


def test_different_resample_seeds_agree_on_a_clear_verdict():
    """Seed sensitivity in the verdict would mean the gate is a coin flip."""
    d, ra, rb = setup(n=400, delta=0.10)
    verdicts = {cmp(d, ra, rb, seed=s).verdict for s in range(5)}
    assert verdicts == {Verdict.IMPROVED}
