"""Statistics, pinned against values that can be looked up.

Two of these tests exist because of a defect the report shipped: a permutation
test at B = 4,000 cannot produce a p-value below 2.5e-4, while Holm over 595
comparisons demands 8.4e-5, so the corrected column was arithmetically forced
to zero and read as a finding about retrieval.
"""

from __future__ import annotations

import math

import numpy as np
import pytest

from rqlab import stats


# -- normal quantile -------------------------------------------------------

@pytest.mark.parametrize(
    "p,expected",
    [
        (0.5, 0.0),
        (0.975, 1.959963985),
        (0.95, 1.644853627),
        (0.8, 0.841621234),
        (0.025, -1.959963985),
        (0.001, -3.090232306),
        (0.999, 3.090232306),
    ],
)
def test_probit_against_published_values(p, expected):
    assert stats.probit(p) == pytest.approx(expected, abs=1e-8)


def test_probit_is_antisymmetric():
    for p in (0.01, 0.1, 0.3, 0.45):
        assert stats.probit(p) == pytest.approx(-stats.probit(1 - p), abs=1e-9)


def test_probit_is_monotone():
    ps = [0.001, 0.01, 0.1, 0.5, 0.9, 0.99, 0.999]
    xs = [stats.probit(p) for p in ps]
    assert xs == sorted(xs)


def test_probit_inverts_norm_cdf():
    for x in (-2.5, -1.0, 0.0, 0.75, 2.2):
        assert stats.probit(stats.norm_cdf(x)) == pytest.approx(x, abs=1e-7)


@pytest.mark.parametrize("p", [0.0, 1.0, -0.1, 1.5])
def test_probit_rejects_out_of_domain(p):
    with pytest.raises(ValueError):
        stats.probit(p)


def test_norm_cdf_known_values():
    assert stats.norm_cdf(0.0) == pytest.approx(0.5)
    assert stats.norm_cdf(1.959963985) == pytest.approx(0.975, abs=1e-9)


# -- incomplete beta and Student's t ---------------------------------------

def test_betai_endpoints():
    assert stats.betai(2.0, 3.0, 0.0) == 0.0
    assert stats.betai(2.0, 3.0, 1.0) == 1.0


def test_betai_symmetry_relation():
    # I_x(a,b) = 1 - I_{1-x}(b,a)
    for a, b, x in [(2.0, 3.0, 0.4), (0.5, 0.5, 0.2), (10.0, 4.0, 0.7)]:
        assert stats.betai(a, b, x) == pytest.approx(
            1.0 - stats.betai(b, a, 1.0 - x), abs=1e-12
        )


def test_betai_matches_a_closed_form_case():
    # I_x(1, b) = 1 - (1-x)^b
    for x in (0.1, 0.5, 0.9):
        assert stats.betai(1.0, 3.0, x) == pytest.approx(1 - (1 - x) ** 3, abs=1e-12)


@pytest.mark.parametrize(
    "t,df,expected",
    [
        (0.0, 10, 1.0),
        (2.228138852, 10, 0.05),
        (1.812461123, 10, 0.10),
        (2.776445105, 4, 0.05),
        (1.959963985, 100000, 0.05000277),
    ],
)
def test_student_t_against_published_critical_values(t, df, expected):
    assert stats.student_t_two_sided(t, df) == pytest.approx(expected, abs=2e-6)


def test_student_t_approaches_the_normal_for_large_df():
    assert stats.student_t_two_sided(1.96, 10_000_000) == pytest.approx(0.05, abs=1e-4)


def test_student_t_is_two_sided_and_symmetric():
    assert stats.student_t_two_sided(2.0, 30) == stats.student_t_two_sided(-2.0, 30)


# -- paired tests ----------------------------------------------------------

def test_paired_t_of_identical_arrays_is_one():
    a = np.array([0.1, 0.5, 0.9])
    assert stats.paired_t(a, a) == 1.0


def test_paired_t_of_a_constant_shift_is_effectively_zero():
    a = np.array([0.1, 0.5, 0.9])
    assert stats.paired_t(a + 0.1, a) < 1e-20


def test_paired_t_of_an_exactly_constant_shift_is_zero():
    a = np.array([1.0, 2.0, 3.0])
    assert stats.paired_t(a + 1.0, a) == 0.0


def test_paired_t_detects_a_consistent_small_difference():
    rng = np.random.default_rng(7)
    base = rng.random(300)
    assert stats.paired_t(base + 0.02, base) < 1e-12


def test_paired_t_ignores_between_query_variance():
    """The whole argument for pairing, as an assertion."""
    rng = np.random.default_rng(11)
    difficulty = rng.random(400)  # huge between-query spread
    a = difficulty + 0.01
    b = difficulty
    assert stats.paired_t(a, b) < 1e-10
    assert stats.unpaired_naive(a, b) > 0.5


def test_paired_t_needs_two_observations():
    assert stats.paired_t(np.array([1.0]), np.array([0.0])) == 1.0


def test_permutation_p_is_never_zero():
    rng = np.random.default_rng(3)
    base = rng.random(120)
    p = stats.paired_permutation(base + 1.0, base, iterations=200)
    assert p > 0.0
    assert p == pytest.approx(1 / 201)


def test_permutation_of_identical_arrays_is_one():
    a = np.array([0.2, 0.4, 0.6])
    assert stats.paired_permutation(a, a, iterations=100) == 1.0


def test_permutation_is_deterministic_given_a_seed():
    rng = np.random.default_rng(5)
    a, b = rng.random(50), rng.random(50)
    p1 = stats.paired_permutation(a, b, iterations=500, seed=99)
    p2 = stats.paired_permutation(a, b, iterations=500, seed=99)
    assert p1 == p2


def test_permutation_resolution_is_the_attainable_floor():
    assert stats.permutation_resolution(4000) == pytest.approx(1 / 4001)
    rng = np.random.default_rng(13)
    base = rng.random(80)
    p = stats.paired_permutation(base + 5.0, base, iterations=4000)
    assert p == pytest.approx(stats.permutation_resolution(4000))


def test_iterations_for_holm_clears_the_threshold():
    for m in (10, 100, 595, 1000):
        b = stats.iterations_for_holm(m)
        assert stats.permutation_resolution(b) < 0.05 / m


def test_iterations_for_holm_is_tight():
    """One fewer resample must not clear it -- otherwise the advice is loose."""
    m = 595
    b = stats.iterations_for_holm(m)
    assert stats.permutation_resolution(b - 1) >= 0.05 / m


def test_the_reported_defect_is_reproducible():
    """B = 4,000 over 595 comparisons cannot yield a Holm-significant result."""
    assert stats.permutation_resolution(4000) > 0.05 / 595


def test_bootstrap_interval_brackets_the_mean_difference():
    rng = np.random.default_rng(17)
    a = rng.random(200) + 0.3
    b = rng.random(200)
    mean, lo, hi = stats.paired_bootstrap(a, b, iterations=2000)
    assert lo < mean < hi


def test_bootstrap_interval_excludes_zero_for_a_real_effect():
    rng = np.random.default_rng(19)
    base = rng.random(200)
    _, lo, hi = stats.paired_bootstrap(base + 0.05, base, iterations=2000)
    assert lo > 0.0


def test_bootstrap_of_empty_input():
    assert stats.paired_bootstrap(np.array([]), np.array([])) == (0.0, 0.0, 0.0)


def test_compare_reports_consistent_sign():
    rng = np.random.default_rng(23)
    base = rng.random(150)
    res = stats.compare(base + 0.04, base, iterations=1000)
    assert res.mean_diff > 0
    assert res.significant_uncorrected
    assert res.ci_excludes_zero
    assert res.n == 150
    assert res.sd_diff >= 0.0


# -- Holm ------------------------------------------------------------------

def test_holm_of_empty_is_empty():
    assert stats.holm([]) == []


def test_holm_single_value_is_unchanged():
    assert stats.holm([0.03]) == [0.03]


def test_holm_matches_the_hand_computation():
    # m=3: sorted 0.01, 0.02, 0.04 -> 3*0.01, 2*0.02, 1*0.04, made monotone
    assert stats.holm([0.01, 0.02, 0.04]) == pytest.approx([0.03, 0.04, 0.04])


def test_holm_is_monotone_in_the_sorted_order():
    ps = [0.001, 0.004, 0.01, 0.2, 0.03, 0.5]
    adj = stats.holm(ps)
    pairs = sorted(zip(ps, adj))
    values = [a for _, a in pairs]
    assert values == sorted(values)


def test_holm_never_decreases_a_p_value():
    ps = [0.001, 0.01, 0.02, 0.3]
    for raw, adj in zip(ps, stats.holm(ps)):
        assert adj >= raw


def test_holm_caps_at_one():
    assert all(p <= 1.0 for p in stats.holm([0.4, 0.6, 0.9]))


def test_holm_is_never_more_conservative_than_bonferroni():
    ps = [0.001, 0.01, 0.02, 0.3, 0.05]
    for adj, raw in zip(stats.holm(ps), ps):
        assert adj <= min(1.0, len(ps) * raw) + 1e-12


# -- power -----------------------------------------------------------------

def test_mde_shrinks_with_more_queries():
    sd = 0.25
    values = [stats.minimum_detectable_effect(sd, n) for n in (50, 100, 500, 5000)]
    assert values == sorted(values, reverse=True)


def test_mde_scales_as_one_over_root_n():
    sd = 0.25
    a = stats.minimum_detectable_effect(sd, 100)
    b = stats.minimum_detectable_effect(sd, 400)
    assert a / b == pytest.approx(2.0, rel=1e-9)


def test_mde_is_proportional_to_the_standard_deviation():
    a = stats.minimum_detectable_effect(0.2, 300)
    b = stats.minimum_detectable_effect(0.4, 300)
    assert b / a == pytest.approx(2.0, rel=1e-9)


def test_required_queries_inverts_the_mde():
    sd = 0.3
    n = stats.required_queries(0.05, sd)
    assert stats.minimum_detectable_effect(sd, n) <= 0.05 + 1e-9


def test_required_queries_grows_as_the_effect_shrinks():
    sd = 0.3
    assert stats.required_queries(0.01, sd) > stats.required_queries(0.02, sd)


def test_required_queries_quadruples_when_the_effect_halves():
    sd = 0.3
    big = stats.required_queries(0.04, sd)
    small = stats.required_queries(0.02, sd)
    assert small / big == pytest.approx(4.0, rel=0.02)


def test_mean_ci_contains_the_mean():
    x = np.array([0.1, 0.2, 0.3, 0.4, 0.5])
    m, lo, hi = stats.mean_ci(x)
    assert lo < m < hi
    assert m == pytest.approx(0.3)


def test_mean_ci_of_a_constant_has_zero_width():
    x = np.array([0.4, 0.4, 0.4])
    m, lo, hi = stats.mean_ci(x)
    assert lo == pytest.approx(m) == pytest.approx(hi)


def test_unpaired_naive_is_symmetric():
    rng = np.random.default_rng(29)
    a, b = rng.random(100), rng.random(100)
    assert stats.unpaired_naive(a, b) == pytest.approx(stats.unpaired_naive(b, a))


def test_unpaired_naive_of_identical_samples_is_one():
    a = np.array([0.1, 0.2, 0.3])
    assert stats.unpaired_naive(a, a) == pytest.approx(1.0)


def test_no_nan_escapes_any_statistic():
    rng = np.random.default_rng(31)
    a, b = rng.random(60), rng.random(60)
    values = [
        stats.paired_t(a, b),
        stats.unpaired_naive(a, b),
        stats.paired_permutation(a, b, iterations=200),
        stats.minimum_detectable_effect(float(np.std(a - b, ddof=1)), 60),
    ]
    assert not any(math.isnan(v) for v in values)
