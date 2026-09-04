"""Tests for the statistical primitives.

These are written against known closed-form answers and against properties
that must hold regardless of implementation, rather than against whatever the
implementation happened to produce on the day. A test that pins a bootstrap
interval to four decimal places tests the random seed, not the statistics.
"""

from __future__ import annotations

import math

import numpy as np
import pytest

from evalharness.stats import (Interval, benjamini_hochberg, betainc, sign_test,
                               minimum_detectable_effect, norm_cdf, norm_ppf,
                               paired_bca_bootstrap, paired_bootstrap,
                               paired_permutation_test, power_paired,
                               required_n, student_t_cdf, student_t_ppf)


# --- normal distribution ---------------------------------------------------


def test_norm_cdf_known_values():
    assert norm_cdf(0.0) == pytest.approx(0.5)
    assert norm_cdf(1.959963985) == pytest.approx(0.975, abs=1e-9)
    assert norm_cdf(-1.959963985) == pytest.approx(0.025, abs=1e-9)


@pytest.mark.parametrize("p,want", [
    (0.5, 0.0), (0.975, 1.959963985), (0.025, -1.959963985),
    (0.95, 1.644853627), (0.99, 2.326347874), (0.001, -3.090232306),
])
def test_norm_ppf_known_values(p, want):
    assert norm_ppf(p) == pytest.approx(want, abs=1e-7)


def test_norm_ppf_inverts_cdf():
    for z in np.linspace(-4.0, 4.0, 81):
        assert norm_ppf(norm_cdf(float(z))) == pytest.approx(float(z), abs=1e-7)


@pytest.mark.parametrize("p", [0.0, 1.0, -0.1, 1.1])
def test_norm_ppf_rejects_out_of_range(p):
    with pytest.raises(ValueError):
        norm_ppf(p)


def test_norm_ppf_tails_use_the_tail_branch():
    # The rational approximation switches branch at 0.02425. A discontinuity
    # there would be invisible in the value -- both branches return something
    # plausible -- so the step across the boundary is compared against the
    # analytic slope dz/dp = 1/phi(z), which at z ~ -1.973 is about 17.6.
    lo = norm_ppf(0.02424)
    hi = norm_ppf(0.02426)
    assert lo < hi
    slope = 1.0 / (math.exp(-lo * lo / 2) / math.sqrt(2 * math.pi))
    assert (hi - lo) == pytest.approx(slope * 2e-5, rel=0.01)


# --- Student's t -----------------------------------------------------------


@pytest.mark.parametrize("df,want", [
    (1, 12.70620), (2, 4.302653), (5, 2.570582), (10, 2.228139),
    (19, 2.093024), (30, 2.042272), (100, 1.983972), (1000, 1.962339),
])
def test_student_t_ppf_matches_published_tables(df, want):
    assert student_t_ppf(0.975, df) == pytest.approx(want, abs=1e-4)


def test_student_t_converges_to_normal():
    assert student_t_ppf(0.975, 1e8) == pytest.approx(norm_ppf(0.975), abs=1e-6)


def test_student_t_cdf_is_symmetric():
    for t in (0.3, 1.0, 2.5):
        assert student_t_cdf(-t, 7) == pytest.approx(1 - student_t_cdf(t, 7), abs=1e-12)


def test_student_t_ppf_inverts_cdf():
    for df in (3, 12, 60):
        for p in (0.01, 0.1, 0.5, 0.9, 0.99):
            assert student_t_cdf(student_t_ppf(p, df), df) == pytest.approx(p, abs=1e-6)


def test_t_is_always_wider_than_z():
    for df in (2, 5, 20, 100):
        assert student_t_ppf(0.975, df) > norm_ppf(0.975)


def test_betainc_endpoints_and_symmetry():
    assert betainc(2.0, 3.0, 0.0) == 0.0
    assert betainc(2.0, 3.0, 1.0) == 1.0
    # I_x(a,b) = 1 - I_{1-x}(b,a)
    assert betainc(2.5, 4.5, 0.3) == pytest.approx(1 - betainc(4.5, 2.5, 0.7), abs=1e-12)


def test_betainc_matches_closed_form_for_integer_parameters():
    # I_x(1, 1) = x
    for x in (0.1, 0.5, 0.9):
        assert betainc(1.0, 1.0, x) == pytest.approx(x, abs=1e-12)
    # I_x(1, 2) = 1 - (1-x)^2
    for x in (0.2, 0.7):
        assert betainc(1.0, 2.0, x) == pytest.approx(1 - (1 - x) ** 2, abs=1e-12)


# --- intervals -------------------------------------------------------------


def test_interval_excludes_zero():
    assert Interval(0.1, 0.05, 0.15, 0.95, "m").excludes_zero
    assert Interval(-0.1, -0.15, -0.05, 0.95, "m").excludes_zero
    assert not Interval(0.1, -0.05, 0.15, 0.95, "m").excludes_zero
    assert not Interval(0.0, 0.0, 0.15, 0.95, "m").excludes_zero


def test_paired_bootstrap_point_is_the_mean_difference():
    rng = np.random.default_rng(0)
    a = rng.random(60)
    b = a + 0.05
    ci = paired_bootstrap(a, b, resamples=2000, seed=1)
    assert ci.point == pytest.approx(0.05, abs=1e-12)


def test_paired_bootstrap_brackets_the_point_estimate():
    rng = np.random.default_rng(2)
    a = rng.random(80)
    b = a + rng.normal(0.03, 0.1, 80)
    ci = paired_bootstrap(a, b, resamples=4000, seed=3)
    assert ci.low <= ci.point <= ci.high


def test_constant_difference_gives_zero_width_interval():
    a = np.linspace(0.1, 0.9, 40)
    b = a + 0.07
    ci = paired_bootstrap(a, b, resamples=1000, seed=4)
    assert ci.width == pytest.approx(0.0, abs=1e-12)


def test_bca_falls_back_when_bootstrap_distribution_is_degenerate():
    # Every resample gives the same mean, so the bias correction is undefined.
    a = np.zeros(30)
    b = np.full(30, 0.07)
    ci = paired_bca_bootstrap(a, b, resamples=500, seed=5)
    assert ci.method == "percentile bootstrap"
    assert ci.point == pytest.approx(0.07)


def test_bca_falls_back_when_the_difference_is_constant_only_to_rounding():
    # The dangerous case: `b - a` is constant to within float rounding, so the
    # bootstrap spread is around 1e-17 and every BCa quantity is estimated
    # from numerical noise. A strict `prop <= 0` guard does not fire here.
    a = np.linspace(0.1, 0.9, 30)
    b = a + 0.07
    assert float(np.std(b - a)) > 0.0
    ci = paired_bca_bootstrap(a, b, resamples=500, seed=5)
    assert ci.method == "percentile bootstrap"
    assert ci.point == pytest.approx(0.07)
    assert ci.width == pytest.approx(0.0, abs=1e-12)


def test_bca_survives_near_constant_data_at_many_sizes():
    # Regression guard for the acceleration pole. Before the scale-based
    # degeneracy check this could produce a division by a quantity near zero.
    for n in (5, 12, 30, 100):
        a = np.linspace(0.0, 1.0, n)
        ci = paired_bca_bootstrap(a, a + 0.02, resamples=400, seed=n)
        assert math.isfinite(ci.low)
        assert math.isfinite(ci.high)
        assert ci.low <= ci.point <= ci.high


def test_bca_and_percentile_agree_on_symmetric_data():
    rng = np.random.default_rng(6)
    diff = rng.normal(0.04, 0.1, 400)
    a = np.zeros(400)
    p = paired_bootstrap(a, diff, resamples=8000, seed=7)
    b = paired_bca_bootstrap(a, diff, resamples=8000, seed=7)
    assert b.low == pytest.approx(p.low, abs=0.006)
    assert b.high == pytest.approx(p.high, abs=0.006)


def test_bca_shifts_relative_to_percentile_on_skewed_data():
    rng = np.random.default_rng(8)
    diff = rng.exponential(0.05, 200)
    a = np.zeros(200)
    p = paired_bootstrap(a, diff, resamples=8000, seed=9)
    b = paired_bca_bootstrap(a, diff, resamples=8000, seed=9)
    assert b.method == "BCa bootstrap"
    assert (b.low, b.high) != (p.low, p.high)


def test_wider_level_gives_wider_interval():
    rng = np.random.default_rng(10)
    a = rng.random(100)
    b = a + rng.normal(0.02, 0.08, 100)
    narrow = paired_bootstrap(a, b, level=0.80, resamples=4000, seed=11)
    wide = paired_bootstrap(a, b, level=0.99, resamples=4000, seed=11)
    assert wide.width > narrow.width


@pytest.mark.parametrize("fn", [paired_bootstrap, paired_bca_bootstrap,
                                paired_permutation_test])
def test_paired_functions_reject_mismatched_shapes(fn):
    with pytest.raises(ValueError):
        fn(np.zeros(10), np.zeros(11))


@pytest.mark.parametrize("fn", [paired_bootstrap, paired_bca_bootstrap,
                                paired_permutation_test])
def test_paired_functions_reject_two_dimensional_input(fn):
    with pytest.raises(ValueError):
        fn(np.zeros((4, 4)), np.zeros((4, 4)))


@pytest.mark.parametrize("fn", [paired_bootstrap, paired_bca_bootstrap,
                                paired_permutation_test])
def test_paired_functions_reject_single_item(fn):
    with pytest.raises(ValueError):
        fn(np.zeros(1), np.zeros(1))


def test_bootstrap_is_deterministic_for_a_seed():
    rng = np.random.default_rng(12)
    a, b = rng.random(50), rng.random(50)
    assert paired_bootstrap(a, b, seed=3) == paired_bootstrap(a, b, seed=3)


def test_bootstrap_differs_across_seeds():
    rng = np.random.default_rng(13)
    a, b = rng.random(50), rng.random(50)
    assert paired_bootstrap(a, b, seed=3) != paired_bootstrap(a, b, seed=4)


# --- permutation test ------------------------------------------------------


def test_permutation_p_never_reaches_zero():
    a = np.zeros(50)
    b = np.ones(50)
    p = paired_permutation_test(a, b, resamples=1000, seed=1)
    assert p > 0.0
    assert p == pytest.approx(1 / 1001)


def test_permutation_p_is_one_when_there_is_no_difference():
    a = np.linspace(0, 1, 40)
    p = paired_permutation_test(a, a.copy(), resamples=1000, seed=1)
    assert p == pytest.approx(1.0)


def test_permutation_detects_a_large_consistent_effect():
    rng = np.random.default_rng(14)
    a = rng.random(60)
    b = a + 0.3
    assert paired_permutation_test(a, b, resamples=4000, seed=2) < 0.01


def test_permutation_is_two_sided():
    rng = np.random.default_rng(15)
    a = rng.random(60)
    up = a + 0.2
    down = a - 0.2
    assert paired_permutation_test(a, up, resamples=4000, seed=3) == pytest.approx(
        paired_permutation_test(a, down, resamples=4000, seed=3))


def test_permutation_p_values_are_uniform_under_the_null():
    # The property that makes a p-value mean anything.
    rng = np.random.default_rng(16)
    ps = []
    for t in range(300):
        d = rng.normal(0.0, 0.1, 40)
        ps.append(paired_permutation_test(np.zeros(40), d, resamples=600, seed=t))
    # Under the null, P(p < 0.05) should be about 0.05. Allow slack for 300
    # trials: the binomial sd is about 1.3 percentage points.
    rate = sum(p < 0.05 for p in ps) / len(ps)
    assert 0.01 <= rate <= 0.11


def test_permutation_resolution_is_bounded_by_sample_size():
    # With n items there are 2^n sign patterns, so a tiny slice cannot produce
    # a small p-value no matter how many resamples are requested. This is why
    # the comparison module skips slices below 5 items.
    a = np.zeros(4)
    b = np.array([1.0, 1.0, 1.0, 1.0])
    p = paired_permutation_test(a, b, resamples=20_000, seed=1)
    assert p >= 1 / 2 ** 4


# --- multiple comparisons --------------------------------------------------


def test_bh_empty():
    assert benjamini_hochberg([]) == []


def test_bh_rejects_nothing_when_all_p_are_large():
    assert benjamini_hochberg([0.4, 0.6, 0.9]) == [False, False, False]


def test_bh_rejects_everything_when_all_p_are_tiny():
    assert benjamini_hochberg([1e-8, 1e-9, 1e-7]) == [True, True, True]


def test_bh_returns_results_in_input_order():
    ps = [0.9, 0.001, 0.5]
    assert benjamini_hochberg(ps) == [False, True, False]


def test_bh_step_up_rejects_a_p_that_fails_its_own_threshold():
    # The step-up property, and the one a naive implementation gets wrong.
    # m=3, fdr=0.05. Thresholds are 0.0167, 0.0333, 0.05.
    # p = [0.01, 0.04, 0.045]: 0.045 <= 0.05 so rank 3 clears, and step-up
    # therefore rejects all three -- including 0.04, which fails its own
    # threshold of 0.0333. An implementation that checks each p against its own
    # threshold independently would reject only 0.01 and 0.045.
    assert benjamini_hochberg([0.01, 0.04, 0.045], fdr=0.05) == [True, True, True]


def test_bh_is_less_strict_than_bonferroni():
    ps = [0.01, 0.02, 0.03, 0.04]
    bh = benjamini_hochberg(ps, fdr=0.05)
    bonferroni = [p <= 0.05 / len(ps) for p in ps]
    assert sum(bh) >= sum(bonferroni)


def test_bh_is_at_least_as_strict_as_uncorrected():
    ps = [0.01, 0.02, 0.03, 0.04, 0.2, 0.5]
    bh = benjamini_hochberg(ps, fdr=0.05)
    raw = [p < 0.05 for p in ps]
    assert sum(bh) <= sum(raw)
    for b, r in zip(bh, raw):
        assert not (b and not r)


def test_bh_handles_ties():
    assert benjamini_hochberg([0.04, 0.04, 0.04], fdr=0.05) == [True, True, True]


def test_bh_controls_false_discovery_rate_under_the_null():
    rng = np.random.default_rng(17)
    any_rejected = 0
    trials = 500
    for _ in range(trials):
        ps = list(rng.random(20))
        if any(benjamini_hochberg(ps, fdr=0.05)):
            any_rejected += 1
    assert any_rejected / trials <= 0.12


def test_uncorrected_testing_fails_where_bh_succeeds():
    # The claim the report makes, verified directly.
    rng = np.random.default_rng(18)
    naive_hits = 0
    trials = 500
    for _ in range(trials):
        ps = list(rng.random(20))
        naive_hits += any(p < 0.05 for p in ps)
    assert naive_hits / trials > 0.5


# --- power -----------------------------------------------------------------


def test_power_rises_with_sample_size():
    powers = [power_paired(n, 0.05, 0.2, resamples=4000, seed=1).power
              for n in (20, 50, 100, 400)]
    assert powers == sorted(powers)


def test_power_rises_with_effect_size():
    powers = [power_paired(100, e, 0.2, resamples=4000, seed=1).power
              for e in (0.01, 0.03, 0.05, 0.15)]
    assert powers == sorted(powers)


def test_power_falls_with_noise():
    powers = [power_paired(100, 0.05, sd, resamples=4000, seed=1).power
              for sd in (0.05, 0.1, 0.2, 0.4)]
    assert powers == sorted(powers, reverse=True)


def test_power_matches_the_normal_approximation():
    # Simulated power should agree with the closed form it is replacing.
    n, effect, sd = 100, 0.05, 0.2
    got = power_paired(n, effect, sd, resamples=40_000, seed=2).power
    ncp = effect * math.sqrt(n) / sd
    want = norm_cdf(ncp - norm_ppf(0.975)) + norm_cdf(-ncp - norm_ppf(0.975))
    assert got == pytest.approx(want, abs=0.02)


def test_power_at_the_null_equals_alpha():
    p = power_paired(200, 0.0, 0.2, resamples=40_000, seed=3)
    assert p.power == pytest.approx(0.05, abs=0.01)


def test_type_m_exceeds_one_when_underpowered():
    p = power_paired(20, 0.02, 0.25, resamples=40_000, seed=4)
    assert p.power < 0.2
    assert p.type_m > 1.5


def test_type_m_approaches_one_when_well_powered():
    p = power_paired(2000, 0.10, 0.2, resamples=8000, seed=5)
    assert p.power > 0.99
    assert p.type_m == pytest.approx(1.0, abs=0.02)


def test_type_s_is_nonzero_only_when_badly_underpowered():
    bad = power_paired(15, 0.01, 0.4, resamples=40_000, seed=6)
    good = power_paired(500, 0.10, 0.2, resamples=8000, seed=7)
    assert bad.type_s > 0.0
    assert good.type_s == 0.0


def test_type_m_and_type_s_are_nan_for_a_null_effect():
    p = power_paired(100, 0.0, 0.2, resamples=4000, seed=8)
    assert math.isnan(p.type_m)
    assert math.isnan(p.type_s)


def test_power_rejects_bad_arguments():
    with pytest.raises(ValueError):
        power_paired(1, 0.05, 0.2)
    with pytest.raises(ValueError):
        power_paired(50, 0.05, 0.0)
    with pytest.raises(ValueError):
        power_paired(50, 0.05, -1.0)


# --- design ----------------------------------------------------------------


def test_mde_shrinks_with_sample_size():
    mdes = [minimum_detectable_effect(n, 0.2) for n in (25, 50, 100, 400)]
    assert mdes == sorted(mdes, reverse=True)


def test_mde_follows_the_inverse_square_root_law():
    assert minimum_detectable_effect(400, 0.2) == pytest.approx(
        minimum_detectable_effect(100, 0.2) / 2, rel=1e-12)


def test_mde_and_required_n_are_inverses():
    sd = 0.2
    for n in (30, 120, 900):
        e = minimum_detectable_effect(n, sd)
        assert required_n(e, sd) == pytest.approx(n, abs=2)


def test_required_n_scales_quadratically():
    sd = 0.2
    assert required_n(0.01, sd) == pytest.approx(required_n(0.02, sd) * 4, rel=0.02)


def test_required_n_delivers_the_power_it_promises():
    sd = 0.2
    effect = 0.03
    n = required_n(effect, sd)
    assert power_paired(n, effect, sd, resamples=40_000, seed=9).power == pytest.approx(
        0.80, abs=0.02)


def test_mde_is_the_effect_detected_at_eighty_percent():
    sd, n = 0.25, 150
    e = minimum_detectable_effect(n, sd)
    assert power_paired(n, e, sd, resamples=40_000, seed=10).power == pytest.approx(
        0.80, abs=0.02)


def test_required_n_rejects_nonpositive_effect():
    with pytest.raises(ValueError):
        required_n(0.0, 0.2)


def test_mde_rejects_tiny_samples():
    with pytest.raises(ValueError):
        minimum_detectable_effect(1, 0.2)


def test_higher_power_demands_more_items():
    assert required_n(0.03, 0.2, power=0.95) > required_n(0.03, 0.2, power=0.80)


def test_stricter_alpha_demands_more_items():
    assert required_n(0.03, 0.2, alpha=0.01) > required_n(0.03, 0.2, alpha=0.05)


# --------------------------------------------------------------------------
# sign_test: the exact distribution the bootstrap was approximating
# --------------------------------------------------------------------------

def test_sign_test_even_split_is_not_significant():
    assert sign_test(50, 100) == pytest.approx(1.0)


def test_sign_test_all_wins_is_decisive():
    assert sign_test(30, 30) < 1e-8


def test_sign_test_is_symmetric():
    assert sign_test(3, 20) == pytest.approx(sign_test(17, 20))


def test_sign_test_matches_hand_computed_small_case():
    # Two-sided P(X<=1) for n=5, p=0.5 is 2*(1+5)/32.
    assert sign_test(1, 5) == pytest.approx(2 * 6 / 32)


def test_sign_test_matches_published_value():
    # n=20, k=5: two-sided exact binomial p = 0.04139 (standard table).
    assert sign_test(5, 20) == pytest.approx(0.04139, abs=1e-5)


def test_sign_test_never_exceeds_one():
    for n in range(1, 60):
        for k in range(n + 1):
            assert 0.0 <= sign_test(k, n) <= 1.0


def test_sign_test_is_monotone_in_the_tail():
    assert sign_test(1, 40) < sign_test(10, 40) < sign_test(19, 40)


def test_sign_test_handles_large_n_without_overflow():
    """Computed in log space; math.comb(4000, 2000) is a 1200-digit integer."""
    assert 0.0 < sign_test(1900, 4000) <= 1.0
    assert sign_test(1700, 4000) < 0.01


def test_sign_test_zero_trials_is_one():
    assert sign_test(0, 0) == 1.0


def test_sign_test_rejects_impossible_counts():
    with pytest.raises(ValueError):
        sign_test(11, 10)
    with pytest.raises(ValueError):
        sign_test(-1, 10)


def test_sign_test_agrees_with_a_normal_approximation_at_large_n():
    """And only with the continuity correction.

    Without the 0.5, the normal approximation gives 0.00729 against the exact
    0.00778 -- 6.7% low, and low in the direction that manufactures
    significance. At the 0.05 boundary that difference decides releases, which
    is a reason to use the exact test when it is available, and it is
    available here.
    """
    n, k = 2000, 940
    naive = 2 * (1 - norm_cdf(abs(k - n / 2) / math.sqrt(n * 0.25)))
    corrected = 2 * (1 - norm_cdf((abs(k - n / 2) - 0.5) / math.sqrt(n * 0.25)))
    exact = sign_test(k, n)
    assert exact == pytest.approx(corrected, rel=0.01)
    assert naive < exact
