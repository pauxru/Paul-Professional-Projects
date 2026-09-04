"""Tests for judge agreement and calibration."""

from __future__ import annotations

import numpy as np
import pytest

from evalharness.agreement import (Agreement, agreement, baseline_margin,
                                   calibration)


def labels(n_pos: int, n_neg: int) -> list[bool]:
    return [True] * n_pos + [False] * n_neg


# --- basic agreement -------------------------------------------------------


def test_perfect_agreement():
    a = agreement([True, False, True, False], [True, False, True, False])
    assert a.observed == 1.0
    assert a.kappa == pytest.approx(1.0)
    assert a.ac1 == pytest.approx(1.0)


def test_total_disagreement():
    a = agreement([True, False, True, False], [False, True, False, True])
    assert a.observed == 0.0
    assert a.kappa < 0.0


def test_agreement_rejects_length_mismatch():
    with pytest.raises(ValueError):
        agreement([True, False], [True])


def test_agreement_rejects_empty():
    with pytest.raises(ValueError):
        agreement([], [])


def test_observed_is_the_raw_rate():
    a = agreement([True] * 8 + [False] * 2, [True] * 9 + [False])
    assert a.observed == pytest.approx(0.9)


def test_n_is_reported():
    assert agreement(labels(5, 5), labels(5, 5)).n == 10


# --- the kappa paradox -----------------------------------------------------


def test_kappa_collapses_on_skewed_data_where_ac1_does_not():
    # The headline claim of section 7. Same judge behaviour, different class
    # balance.
    rng = np.random.default_rng(0)
    n = 2000
    balanced_truth = rng.random(n) < 0.5
    skewed_truth = rng.random(n) < 0.95
    flip = rng.random(n) < 0.08

    balanced = agreement(balanced_truth.tolist(),
                         np.where(flip, ~balanced_truth, balanced_truth).tolist())
    skewed = agreement(skewed_truth.tolist(),
                       np.where(flip, ~skewed_truth, skewed_truth).tolist())

    assert balanced.observed == pytest.approx(skewed.observed, abs=0.02)
    assert balanced.kappa > 0.80
    assert skewed.kappa < 0.55
    assert skewed.ac1 > 0.85


def test_prevalence_index_rises_with_skew():
    rng = np.random.default_rng(1)
    n = 2000
    indices = []
    for prevalence in (0.5, 0.7, 0.9, 0.98):
        truth = rng.random(n) < prevalence
        flip = rng.random(n) < 0.05
        indices.append(agreement(truth.tolist(),
                                 np.where(flip, ~truth, truth).tolist()).prevalence_index)
    assert indices == sorted(indices)


def test_paradoxical_flag_fires_on_skew_and_not_on_balance():
    rng = np.random.default_rng(2)
    n = 2000
    balanced_truth = rng.random(n) < 0.5
    skewed_truth = rng.random(n) < 0.96
    flip = rng.random(n) < 0.07
    balanced = agreement(balanced_truth.tolist(),
                         np.where(flip, ~balanced_truth, balanced_truth).tolist())
    skewed = agreement(skewed_truth.tolist(),
                       np.where(flip, ~skewed_truth, skewed_truth).tolist())
    assert not balanced.paradoxical
    assert skewed.paradoxical


def test_ac1_is_more_stable_than_kappa_across_prevalence():
    rng = np.random.default_rng(3)
    n = 3000
    kappas, ac1s = [], []
    for prevalence in (0.5, 0.65, 0.8, 0.9, 0.96):
        truth = rng.random(n) < prevalence
        flip = rng.random(n) < 0.06
        a = agreement(truth.tolist(), np.where(flip, ~truth, truth).tolist())
        kappas.append(a.kappa)
        ac1s.append(a.ac1)
    assert (max(kappas) - min(kappas)) > (max(ac1s) - min(ac1s))


def test_all_positive_judge_on_all_positive_data():
    # Perfect observed agreement, undefined chance correction. Neither
    # statistic should blow up, and the verdict should not say "excellent".
    a = agreement([True] * 50, [True] * 50)
    assert a.observed == 1.0
    assert np.isfinite(a.kappa)
    assert np.isfinite(a.ac1)


def test_bias_index_detects_a_lenient_judge():
    lenient = labels(80, 20)
    truth = labels(50, 50)
    a = agreement(lenient, truth)
    assert a.bias_index == pytest.approx(0.30, abs=1e-9)
    assert a.judge_positive_rate == pytest.approx(0.80)
    assert a.human_positive_rate == pytest.approx(0.50)


def test_bias_index_is_zero_for_matched_marginals():
    a = agreement(labels(50, 50), labels(50, 50)[::-1])
    assert a.bias_index == pytest.approx(0.0)


def test_trustworthy_requires_more_than_raw_agreement():
    # A judge that says yes to everything on skewed data has high observed
    # agreement and should not be called trustworthy.
    truth = labels(95, 5)
    always_yes = [True] * 100
    a = agreement(always_yes, truth)
    assert a.observed == pytest.approx(0.95)
    assert not a.trustworthy


def test_neither_kappa_nor_ac1_catches_a_constant_judge():
    # The finding behind `informative`. AC1 was built so that prevalence does
    # not collapse it, and the same property means degeneracy does not either.
    truth = labels(95, 5)
    a = agreement([True] * 100, truth)
    assert a.ac1 > 0.90
    assert a.kappa == pytest.approx(0.0)
    assert not a.informative


def test_informative_compares_against_the_constant_labeller():
    # A judge that flips 8% of labels at random on 92%-pass data achieves the
    # same agreement as answering "pass" every time -- so it is worth nothing,
    # while showing 92% agreement and an AC1 near 0.89.
    rng = np.random.default_rng(11)
    n = 5000
    truth = rng.random(n) < 0.92
    flip = rng.random(n) < 0.08
    judged = np.where(flip, ~truth, truth)
    a = agreement(judged.tolist(), truth.tolist())
    assert a.observed == pytest.approx(0.92, abs=0.01)
    assert a.baseline_agreement == pytest.approx(0.92, abs=0.01)
    assert a.ac1 > 0.85
    assert not a.informative
    assert not a.trustworthy


def test_a_genuinely_good_judge_is_informative():
    truth = labels(60, 40)
    good = labels(58, 42)
    a = agreement(good, truth)
    assert a.baseline_agreement == pytest.approx(0.60)
    assert a.observed > 0.60
    assert a.informative


def test_uninformative_verdict_names_the_baseline():
    a = agreement([True] * 100, labels(95, 5))
    v = a.verdict()
    assert "uninformative" in v
    assert "95.0%" in v


def test_verdict_is_a_nonempty_explanation():
    a = agreement(labels(95, 5), [True] * 100)
    assert isinstance(a.verdict(), str)
    assert len(a.verdict()) > 20


def test_agreement_is_symmetric_in_observed_but_not_in_bias():
    x, y = labels(70, 30), labels(50, 50)
    a, b = agreement(x, y), agreement(y, x)
    assert a.observed == pytest.approx(b.observed)
    assert a.judge_positive_rate == pytest.approx(b.human_positive_rate)


def test_kappa_is_zero_for_independent_judgements():
    rng = np.random.default_rng(4)
    n = 20000
    truth = (rng.random(n) < 0.5).tolist()
    independent = (rng.random(n) < 0.5).tolist()
    assert agreement(truth, independent).kappa == pytest.approx(0.0, abs=0.03)


def test_agreement_accepts_numpy_arrays():
    a = agreement(np.array([True, False, True]), np.array([True, False, True]))
    assert a.observed == 1.0


def test_agreement_dataclass_is_frozen():
    a = agreement(labels(5, 5), labels(5, 5))
    assert isinstance(a, Agreement)
    with pytest.raises(Exception):
        a.observed = 0.0


# --- calibration -----------------------------------------------------------


def test_perfect_calibration():
    x = np.linspace(0.1, 0.9, 50)
    cal = calibration(x, x)
    assert cal.mean_error == pytest.approx(0.0)
    assert cal.mean_abs_error == pytest.approx(0.0)
    assert cal.correlation == pytest.approx(1.0)
    assert cal.rank_correlation == pytest.approx(1.0)
    assert cal.usable_for_ranking
    assert cal.usable_for_absolute_scores


def test_constant_offset_preserves_ranking_and_destroys_calibration():
    # The distinction the whole class exists to make.
    x = np.linspace(0.1, 0.7, 60)
    cal = calibration(x + 0.25, x)
    assert cal.rank_correlation == pytest.approx(1.0)
    assert cal.mean_error == pytest.approx(0.25)
    assert cal.usable_for_ranking
    assert not cal.usable_for_absolute_scores


def test_mean_error_is_signed():
    x = np.linspace(0.1, 0.7, 20)
    assert calibration(x - 0.1, x).mean_error < 0
    assert calibration(x + 0.1, x).mean_error > 0


def test_mean_abs_error_is_unsigned():
    x = np.linspace(0.1, 0.7, 20)
    assert calibration(x - 0.1, x).mean_abs_error == pytest.approx(0.1)


def test_random_judge_is_usable_for_neither():
    rng = np.random.default_rng(5)
    truth = rng.random(300)
    noise = rng.random(300)
    cal = calibration(noise, truth)
    assert abs(cal.rank_correlation) < 0.3
    assert not cal.usable_for_ranking
    assert not cal.usable_for_absolute_scores


def test_rank_correlation_survives_a_monotone_distortion():
    # Squaring is monotone on [0,1]: order preserved, values wrecked.
    x = np.linspace(0.05, 0.95, 80)
    cal = calibration(x ** 2, x)
    assert cal.rank_correlation == pytest.approx(1.0)
    assert cal.correlation < 1.0
    assert cal.mean_abs_error > 0.05


def test_calibration_rejects_shape_mismatch():
    with pytest.raises(ValueError):
        calibration(np.zeros(5), np.zeros(6))


def test_calibration_rejects_single_item():
    with pytest.raises(ValueError):
        calibration(np.zeros(1), np.zeros(1))


def test_correlation_of_a_constant_vector_is_zero_not_nan():
    # A judge that returns the same score for everything has no correlation;
    # returning NaN here propagates into every downstream comparison and turns
    # a diagnostic into a crash.
    x = np.linspace(0.1, 0.9, 30)
    cal = calibration(np.full(30, 0.5), x)
    assert cal.correlation == 0.0
    assert cal.rank_correlation == 0.0
    assert not cal.usable_for_ranking


def test_rank_correlation_averages_ties():
    # Coarse judge scales make ties routine; ranking them by position rather
    # than averaging would invent an ordering that is not in the data.
    judge = np.array([1.0, 1.0, 2.0, 2.0, 3.0])
    human = np.array([0.1, 0.2, 0.3, 0.4, 0.5])
    cal = calibration(judge, human)
    assert 0.9 < cal.rank_correlation <= 1.0


def test_all_ties_give_zero_rank_correlation():
    judge = np.full(20, 4.0)
    human = np.linspace(0, 1, 20)
    assert calibration(judge, human).rank_correlation == 0.0


def test_calibration_str_is_informative():
    x = np.linspace(0.1, 0.9, 20)
    s = str(calibration(x + 0.1, x))
    assert "bias" in s and "MAE" in s


# --------------------------------------------------------------------------
# baseline_margin: is the judge better than a hard-coded string?
# --------------------------------------------------------------------------

def test_always_yes_judge_is_not_informative():
    """The assertion that found the bug.

    An always-yes judge on 95%-pass data scores 95% agreement and an AC1 above
    0.94. Both of the statistics the literature recommends rate it highly. It
    is a constant.
    """
    human = [1] * 95 + [0] * 5
    judge = [1] * 100
    a = agreement(judge, human)
    assert a.observed == pytest.approx(0.95)
    assert a.ac1 > 0.90
    assert not a.informative
    assert not a.trustworthy


def test_always_no_judge_is_not_informative():
    human = [1] * 5 + [0] * 95
    a = agreement([0] * 100, human)
    assert not a.informative


def test_a_perfect_judge_is_informative_even_when_skewed():
    human = [1] * 92 + [0] * 8
    a = agreement(list(human), human)
    assert a.informative
    assert a.baseline_margin == pytest.approx(0.08)


def test_baseline_margin_closed_form_matches_the_definition():
    """margin == (d - c)/n on pass-majority data, and a never enters it."""
    rng = np.random.default_rng(4)
    for _ in range(40):
        human = (rng.random(200) < 0.8).astype(int)
        judge = np.where(rng.random(200) < 0.15, 1 - human, human)
        a = agreement(judge.tolist(), human.tolist())
        direct = float((judge == human).mean()) - a.baseline_agreement
        assert a.baseline_margin == pytest.approx(direct, abs=1e-12)


def test_baseline_margin_uses_only_the_discordant_cells():
    _a, _b, c, d = agreement([1, 1, 0, 0], [1, 1, 1, 0]).counts
    assert agreement([1, 1, 0, 0], [1, 1, 1, 0]).baseline_margin == pytest.approx((d - c) / 4)


def test_baseline_margin_is_negative_for_a_harmful_judge():
    human = [1] * 97 + [0] * 3
    judge = [0] * 10 + [1] * 87 + [0] * 3
    a = agreement(judge, human)
    assert a.baseline_margin < 0
    assert not a.informative


def test_baseline_margin_p_is_small_for_a_clearly_useful_judge():
    human = ([1] * 50 + [0] * 50) * 10
    judge = list(human)
    assert agreement(judge, human).baseline_margin_p < 1e-6


def test_baseline_margin_p_is_large_for_a_coin_flip_margin():
    rng = np.random.default_rng(11)
    human = (rng.random(3000) < 0.92).astype(int)
    judge = np.where(rng.random(3000) < 0.08, 1 - human, human)
    a = agreement(judge.tolist(), human.tolist())
    assert a.baseline_margin_p > 0.05
    assert not a.informative


def test_informative_requires_significance_not_just_a_positive_point():
    """Regression test for the first version of `informative`.

    It compared two point estimates and called any positive difference a win,
    which is precisely the error the rest of this repository is about. It
    passed a judge whose margin was +0.004 with an interval straddling zero.
    """
    rng = np.random.default_rng(3)
    for _ in range(60):
        human = (rng.random(400) < 0.9).astype(int)
        judge = np.where(rng.random(400) < 0.1, 1 - human, human)
        a = agreement(judge.tolist(), human.tolist())
        if 0.0 < a.baseline_margin < 0.01:
            assert not a.informative
            return


def test_bootstrap_margin_agrees_with_the_exact_test():
    """The bootstrap was written first. This is the evidence they agree."""
    rng = np.random.default_rng(21)
    for prevalence in (0.5, 0.75, 0.92):
        human = (rng.random(1500) < prevalence).astype(int)
        judge = np.where(rng.random(1500) < 0.1, 1 - human, human)
        a = agreement(judge.tolist(), human.tolist())
        ci = baseline_margin(judge, human, resamples=3000, seed=2)
        assert ci.point == pytest.approx(a.baseline_margin, abs=1e-12)
        assert ci.excludes_zero == (a.baseline_margin_p < 0.05)


def test_baseline_margin_rejects_mismatched_shapes():
    with pytest.raises(ValueError):
        baseline_margin(np.array([1, 0, 1]), np.array([1, 0]))


def test_baseline_margin_rejects_tiny_input():
    with pytest.raises(ValueError):
        baseline_margin(np.array([1]), np.array([1]))


def test_counts_sum_to_n():
    rng = np.random.default_rng(8)
    human = (rng.random(300) < 0.7).astype(int)
    judge = (rng.random(300) < 0.7).astype(int)
    a = agreement(judge.tolist(), human.tolist())
    assert sum(a.counts) == a.n == 300


def test_verdict_names_the_margin_when_uninformative():
    a = agreement([1] * 100, [1] * 95 + [0] * 5)
    v = a.verdict()
    assert "uninformative" in v
    assert "0.0000" in v
