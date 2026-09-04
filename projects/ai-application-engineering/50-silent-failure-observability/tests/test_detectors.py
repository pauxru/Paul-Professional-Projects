"""Tests for the detector statistics and the calibration discipline.

Most of these run on synthetic arrays rather than on the corpus, because the properties
being checked are properties of the statistics -- PSI is zero on identical samples, MMD is
symmetric, a CUSUM ignores a downward shift -- and testing them through ninety days of
generated text would be slow and would confuse a broken statistic with a broken simulator.

The calibration tests are the ones that matter most. Every false alarm this panel ever
produced came from a threshold that was measuring the wrong thing.
"""

from __future__ import annotations

import numpy as np
import pytest

from src import detectors, stream


@pytest.fixture(scope="module")
def rng():
    return np.random.default_rng(1234)


# ---------------------------------------------------------------------------
# PSI


def test_psi_of_a_sample_against_itself_is_about_zero(rng):
    x = rng.normal(size=(400, 16))
    assert detectors.population_stability_index(x, x) < 1e-9


def test_psi_is_non_negative(rng):
    a = rng.normal(size=(400, 16))
    b = rng.normal(loc=0.4, size=(400, 16))
    assert detectors.population_stability_index(a, b) >= 0.0


def test_psi_grows_with_the_size_of_the_shift(rng):
    ref = rng.normal(size=(600, 16))
    small = detectors.population_stability_index(ref, rng.normal(loc=0.2, size=(600, 16)))
    large = detectors.population_stability_index(ref, rng.normal(loc=1.2, size=(600, 16)))
    assert large > small


def test_psi_is_finite_when_a_bin_is_empty(rng):
    """Without the probability floor an empty bin sends one term to infinity and PSI
    becomes a report about sample size."""
    ref = rng.normal(size=(400, 8))
    far = rng.normal(loc=25.0, size=(50, 8))
    assert np.isfinite(detectors.population_stability_index(ref, far))


def test_psi_drops_a_dimension_that_is_constant_in_the_reference(rng):
    """A constant reference dimension cannot support a quantile binning.

    Every edge collapses to the same value, and the two infinite outer edges then map the
    whole real line into a single bin -- so a genuine shift in that dimension would score
    exactly zero drift while still diluting the average. Dropping it leaves the answer
    unchanged, which is the point: the dimension carried no information either way.
    """
    ref = rng.normal(size=(400, 8))
    cur = rng.normal(loc=0.5, size=(400, 8))
    plain = detectors.population_stability_index(ref, cur)
    padded = detectors.population_stability_index(
        np.hstack([ref, np.zeros((400, 1))]), np.hstack([cur, np.full((400, 1), 9.0)])
    )
    assert np.isfinite(padded)
    assert padded == pytest.approx(plain)


def test_psi_drops_a_dimension_containing_non_finite_values(rng):
    ref = rng.normal(size=(300, 5))
    ref[0, 2] = np.inf
    cur = rng.normal(loc=0.5, size=(300, 5))
    assert np.isfinite(detectors.population_stability_index(ref, cur))


def test_psi_of_empty_input_is_zero(rng):
    assert detectors.population_stability_index(np.zeros((0, 4)), rng.normal(size=(10, 4))) == 0.0
    assert detectors.population_stability_index(rng.normal(size=(10, 4)), np.zeros((0, 4))) == 0.0


def test_psi_is_blind_to_a_change_that_leaves_the_margins_intact(rng):
    """The documented weakness, made a test rather than a claim.

    Swapping the pairing between two dimensions changes the joint distribution completely
    and leaves both marginals exactly as they were. PSI, being a sum of marginal
    comparisons, cannot see it. This is the reason MMD is in the panel.
    """
    a = rng.normal(size=600)
    b = rng.normal(size=600)
    ref = np.column_stack([a, b])
    # Same two marginals, independently reshuffled: identical margins, different joint.
    cur = np.column_stack([rng.permutation(a), rng.permutation(b)])
    assert detectors.population_stability_index(ref, cur) < 0.02


def _reference_psi(reference: np.ndarray, current: np.ndarray, bins: int = 10) -> float:
    """A deliberately naive, obviously-correct PSI, used to check the optimised one.

    This exists because the first version of the alignment test compared `PSIReference`
    against `population_stability_index` -- which, since the optimisation, *is*
    `PSIReference`. The test compared the code to itself and passed while the mutation
    harness broke the indexing. An oracle has to be independent of the thing it judges.
    """
    total, counted = 0.0, 0
    for d in range(reference.shape[1]):
        column = reference[:, d]
        if float(np.ptp(column)) == 0.0 or not np.all(np.isfinite(column)):
            continue
        edges = np.quantile(column, np.linspace(0.0, 1.0, bins + 1))
        edges[0], edges[-1] = -np.inf, np.inf
        edges = np.unique(edges)
        if len(edges) < 3:
            continue
        ref_counts, _ = np.histogram(column, bins=edges)
        cur_counts, _ = np.histogram(current[:, d], bins=edges)
        ref_p = np.clip(ref_counts / max(1, ref_counts.sum()), 1e-6, None)
        cur_p = np.clip(cur_counts / max(1, cur_counts.sum()), 1e-6, None)
        total += float(np.sum((cur_p - ref_p) * np.log(cur_p / ref_p)))
        counted += 1
    return total / max(1, counted)


def test_psi_matches_an_independent_reference_implementation(rng):
    ref = rng.normal(size=(500, 12))
    for loc in (0.0, 0.3, 1.5):
        cur = rng.normal(loc=loc, size=(300, 12))
        assert detectors.population_stability_index(ref, cur) == pytest.approx(
            _reference_psi(ref, cur), abs=1e-12
        )


def test_psi_reference_object_matches_the_one_shot_function(rng):
    """The optimisation must not change the statistic, only the arithmetic."""
    ref = rng.normal(size=(500, 12))
    for loc in (0.0, 0.3, 1.5):
        cur = rng.normal(loc=loc, size=(300, 12))
        precomputed = detectors.PSIReference(ref).score(cur)
        one_shot = detectors.population_stability_index(ref, cur)
        assert abs(precomputed - one_shot) < 1e-12


def test_psi_reference_survives_a_dropped_dimension(rng):
    """A skipped degenerate dimension must not misalign the remaining ones.

    Column 0 is constant *and* column 3 is made degenerate, so the kept-dimension list is
    genuinely sparse. Scored against the independent oracle rather than against the
    optimised implementation, because a positional counter gets this consistently wrong in
    both directions at once and self-comparison cannot see it.
    """
    ref = rng.normal(size=(300, 7))
    cur = rng.normal(loc=0.8, size=(300, 7))
    ref[:, 0] = 0.0
    cur[:, 0] = 0.0
    ref[:, 3] = 5.0
    cur[:, 3] = 5.0
    assert detectors.population_stability_index(ref, cur) == pytest.approx(
        _reference_psi(ref, cur), abs=1e-12
    )


def test_psi_reference_of_empty_reference_scores_zero(rng):
    assert detectors.PSIReference(np.zeros((0, 4))).score(rng.normal(size=(5, 4))) == 0.0


# ---------------------------------------------------------------------------
# MMD


def test_mmd_of_a_distribution_against_itself_is_near_zero(rng):
    x = rng.normal(size=(150, 8))
    y = rng.normal(size=(150, 8))
    assert abs(detectors.mmd_squared(x, y, gamma=0.1)) < 0.05


def test_mmd_is_symmetric(rng):
    x = rng.normal(size=(80, 8))
    y = rng.normal(loc=0.6, size=(80, 8))
    assert abs(detectors.mmd_squared(x, y, 0.2) - detectors.mmd_squared(y, x, 0.2)) < 1e-12


def test_mmd_grows_with_separation(rng):
    x = rng.normal(size=(150, 8))
    near = detectors.mmd_squared(x, rng.normal(loc=0.3, size=(150, 8)), 0.2)
    far = detectors.mmd_squared(x, rng.normal(loc=2.0, size=(150, 8)), 0.2)
    assert far > near


def test_mmd_sees_what_psi_cannot(rng):
    """The complement of `test_psi_is_blind_to_a_change_that_leaves_the_margins_intact`.

    Two dimensions that are perfectly correlated, versus the same two marginals made
    independent. PSI scores this near zero; a kernel two-sample test on the joint
    distribution should not.
    """
    a = rng.normal(size=400)
    ref = np.column_stack([a, a])
    cur = np.column_stack([rng.permutation(a), rng.permutation(a)])
    gamma = detectors.median_heuristic_gamma(ref, rng)
    assert detectors.mmd_squared(ref, cur, gamma) > 0.05


def test_mmd_of_tiny_samples_is_zero_not_a_division_error(rng):
    assert detectors.mmd_squared(rng.normal(size=(1, 4)), rng.normal(size=(5, 4)), 0.1) == 0.0


def test_median_heuristic_gamma_is_positive_and_scale_aware(rng):
    tight = detectors.median_heuristic_gamma(rng.normal(scale=0.1, size=(200, 8)), rng)
    wide = detectors.median_heuristic_gamma(rng.normal(scale=10.0, size=(200, 8)), rng)
    assert tight > 0 and wide > 0
    assert tight > wide


def test_median_heuristic_gamma_of_a_single_point_does_not_divide_by_zero(rng):
    assert detectors.median_heuristic_gamma(np.zeros((1, 4)), rng) == 1.0


# ---------------------------------------------------------------------------
# CUSUM


def test_cusum_stays_at_zero_on_target():
    scores = detectors.cusum([1.0] * 50, target=1.0, slack=0.0)
    assert max(scores) == 0.0


def test_cusum_accumulates_an_upward_shift():
    scores = detectors.cusum([1.0] * 20 + [2.0] * 20, target=1.0, slack=0.0)
    assert scores[19] == 0.0
    assert scores[-1] == pytest.approx(20.0)


def test_cusum_is_one_sided_and_ignores_a_downward_shift():
    scores = detectors.cusum([1.0] * 20 + [0.0] * 20, target=1.0, slack=0.0)
    assert max(scores) == 0.0


def test_cusum_slack_absorbs_noise():
    series = [1.0, 1.2, 0.9, 1.1, 1.05]
    assert max(detectors.cusum(series, target=1.0, slack=0.5)) == 0.0
    assert max(detectors.cusum(series, target=1.0, slack=0.0)) > 0.0


def test_cusum_reset_clears_the_accumulator():
    series = [3.0] * 10 + [1.0] * 10
    without = detectors.cusum(series, target=1.0, slack=0.0)
    with_reset = detectors.cusum(series, target=1.0, slack=0.0, reset_at=10)
    assert without[-1] > 0.0
    assert with_reset[-1] == 0.0


def test_a_cusum_on_pure_noise_eventually_crosses_its_own_reference_quantile():
    """Why `calibrate` is the wrong tool for a CUSUM, demonstrated rather than asserted.

    A CUSUM is a reflected random walk. Calibrated against the quantile of its own values
    over a short reference window, it will cross that threshold on stationary noise given
    a long enough horizon -- which is exactly the false alarm the first version of this
    panel produced on the healthy control.
    """
    rng = np.random.default_rng(0)
    trials = 20
    crossings = 0
    for _ in range(trials):
        rates = rng.normal(loc=0.02, scale=0.013, size=90).tolist()
        scores = detectors.cusum(rates, target=float(np.mean(rates[:30])), slack=float(np.std(rates[:30])))
        naive = detectors.calibrate(scores, floor=0.0)
        if detectors.first_alert(scores, naive) >= 0:
            crossings += 1
    # Measured at 7 in 20. The threshold was calibrated for a 1% per-day false positive
    # rate; an order of magnitude above that is the finding.
    assert crossings / trials > 10 * detectors.ALPHA


def test_bootstrap_threshold_is_far_higher_than_the_naive_one():
    rng = np.random.default_rng(0)
    rates = rng.normal(loc=0.02, scale=0.013, size=90).tolist()
    scores = detectors.cusum(rates, target=float(np.mean(rates[:30])), slack=float(np.std(rates[:30])))
    naive = detectors.calibrate(scores, floor=0.0)
    honest = detectors.bootstrap_cusum_threshold(rates[:30], horizon=60)
    assert honest > naive


def test_bootstrap_threshold_holds_the_false_alarm_rate_on_stationary_noise():
    """The property the threshold is supposed to have, measured over many fresh streams."""
    rng = np.random.default_rng(7)
    alarms = 0
    trials = 60
    for _ in range(trials):
        rates = np.clip(rng.normal(loc=0.02, scale=0.013, size=90), 0.0, None).tolist()
        threshold = detectors.bootstrap_cusum_threshold(rates[:30], horizon=60, trials=200)
        scores = detectors.cusum(
            rates, target=float(np.mean(rates[:30])), slack=float(np.std(rates[:30])),
            reset_at=30,
        )
        if detectors.first_alert(scores, threshold) >= 0:
            alarms += 1
    assert alarms <= trials * 0.15


def test_bootstrap_threshold_of_an_empty_window_never_alerts():
    assert detectors.bootstrap_cusum_threshold([], horizon=10) == float("inf")


# ---------------------------------------------------------------------------
# alerting rules


def test_first_alert_requires_persistence():
    scores = [0.0] * 30 + [5.0, 0.0, 5.0, 5.0, 0.0, 5.0, 5.0, 5.0]
    assert detectors.first_alert(scores, threshold=1.0, persistence=3) == 35


def test_first_alert_returns_minus_one_when_never_crossed():
    assert detectors.first_alert([0.0] * 90, threshold=1.0) == -1


def test_no_detector_may_alert_inside_the_reference_window():
    scores = [99.0] * 90
    assert detectors.first_alert(scores, threshold=1.0) == detectors.REFERENCE_DAYS


def test_alert_days_counts_every_alerting_day_not_just_the_first():
    scores = [0.0] * 30 + [5.0] * 10
    assert len(detectors.alert_days(scores, threshold=1.0, persistence=3)) == 8


def test_calibrate_respects_its_floor():
    assert detectors.calibrate([0.0] * 40, floor=0.25) == 0.25


def test_calibrate_uses_only_the_reference_window():
    """A threshold that saw the degraded days would calibrate itself out of alerting."""
    scores = [0.01] * detectors.REFERENCE_DAYS + [100.0] * 60
    assert detectors.calibrate(scores, floor=0.0) < 1.0


def test_calibrate_of_an_empty_series_returns_the_floor():
    assert detectors.calibrate([], floor=0.5) == 0.5


# ---------------------------------------------------------------------------
# the panel as a whole


@pytest.fixture(scope="module")
def healthy():
    return list(stream.generate("healthy"))


def test_every_detector_returns_one_score_per_day(healthy):
    for fn in detectors.DETECTORS:
        result = fn(healthy, stream.DAYS)
        assert len(result.scores) == stream.DAYS, result.name


def test_every_detector_score_is_finite(healthy):
    for fn in detectors.DETECTORS:
        result = fn(healthy, stream.DAYS)
        assert all(np.isfinite(s) for s in result.scores), result.name


def test_no_detector_alerts_on_the_healthy_control(healthy):
    """The single most important test in the file. Every detector is calibrated to the
    same false positive rate; if one of them alerts here, its detections elsewhere are
    not evidence of anything."""
    for fn in detectors.DETECTORS:
        result = fn(healthy, stream.DAYS)
        assert detectors.first_alert(result.scores, result.threshold) == -1, result.name


def test_detector_names_are_unique(healthy):
    names = [fn(healthy, stream.DAYS).name for fn in detectors.DETECTORS]
    assert len(names) == len(set(names))


def test_only_the_canary_costs_model_calls(healthy):
    paid = [fn(healthy, stream.DAYS) for fn in detectors.DETECTORS]
    assert sum(1 for r in paid if r.calls_per_day > 0) == 1


def test_apm_is_flat_because_nothing_ever_errors_or_slows_down():
    """The control detector, on the worst regression in the panel."""
    turns = list(stream.generate("model-swap"))
    result = detectors.apm_baseline(turns, stream.DAYS)
    assert detectors.first_alert(result.scores, result.threshold) == -1


def test_detectors_never_read_ground_truth():
    """Enforced by reading the source rather than by trusting the convention.

    A detector that peeked at `is_degraded` or `quality` would score perfectly and prove
    nothing, and it is an easy mistake to make while debugging.
    """
    import inspect

    source = inspect.getsource(detectors)
    body = source.split("# detectors", 1)[1]
    assert "is_degraded" not in body
    assert ".quality" not in body
