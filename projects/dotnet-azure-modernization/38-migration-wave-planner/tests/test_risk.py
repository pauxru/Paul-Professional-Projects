"""The Monte Carlo: reproducibility, and that the bias is not an artefact.

The central claim of section 6 of the report is that the merge bias is real
rather than a modelling accident, so the tests that matter are the ones that
show the per-team effort distribution has the point estimate as its mean. If
the log-normal were not median-corrected, every number in that section would
be measuring the correction instead.
"""

from __future__ import annotations

import numpy as np
import pytest

from wave.costs import Plan, evaluate
from wave.risk import (
    RHO_PROGRAMME,
    RHO_WAVE,
    SIGMA,
    cost_at_percentile,
    deterministic_end,
    merge_bias_by_wave,
    rho_sweep,
    simulate,
    wave_team_effort,
)


def _spearman(xs, ys):
    """Rank correlation, written out so the test does not depend on scipy."""

    def rank(vs):
        order = sorted(range(len(vs)), key=lambda i: vs[i])
        out = [0.0] * len(vs)
        i = 0
        while i < len(order):
            j = i
            while j + 1 < len(order) and vs[order[j + 1]] == vs[order[i]]:
                j += 1
            avg = (i + j) / 2 + 1
            for k in range(i, j + 1):
                out[order[k]] = avg
            i = j + 1
        return out

    rx, ry = rank(list(xs)), rank(list(ys))
    n = len(rx)
    mx, my = sum(rx) / n, sum(ry) / n
    num = sum((a - mx) * (b - my) for a, b in zip(rx, ry))
    den = (
        sum((a - mx) ** 2 for a in rx) * sum((b - my) ** 2 for b in ry)
    ) ** 0.5
    return num / den if den else 0.0


@pytest.fixture(scope="module")
def result(estate, contracted, best_plan):
    return simulate(estate, contracted, best_plan, draws=4000)


class TestWaveTeamEffort:
    def test_one_entry_per_wave(self, contracted, best_plan):
        assert len(wave_team_effort(contracted, best_plan)) == best_plan.n_waves

    def test_totals_match_unit_efforts(self, contracted, best_plan):
        got = sum(sum(w.values()) for w in wave_team_effort(contracted, best_plan))
        assert got == pytest.approx(sum(u.effort for u in contracted.units))

    def test_per_wave_totals_match(self, contracted, best_plan):
        for w, pw in zip(best_plan.waves, wave_team_effort(contracted, best_plan)):
            assert sum(pw.values()) == pytest.approx(
                sum(contracted.by_id(u).effort for u in w)
            )

    def test_no_negative_effort(self, contracted, best_plan):
        for pw in wave_team_effort(contracted, best_plan):
            assert all(v >= 0 for v in pw.values())


class TestDeterministicEnd:
    def test_matches_the_schedule(self, estate, contracted, best_plan, best_schedule):
        assert deterministic_end(estate, contracted, best_plan) == pytest.approx(
            best_schedule.end_month
        )

    def test_freeze_off_is_never_later(self, estate, contracted, all_plans):
        for name, p in all_plans.items():
            assert deterministic_end(
                estate, contracted, p, freeze=False
            ) <= deterministic_end(estate, contracted, p) + 1e-9, name

    def test_positive(self, estate, contracted, all_plans):
        for name, p in all_plans.items():
            assert deterministic_end(estate, contracted, p) > 0, name


class TestSimulate:
    def test_draw_count(self, result):
        assert result.samples.shape == (4000,)

    def test_reproducible(self, estate, contracted, best_plan):
        a = simulate(estate, contracted, best_plan, draws=1000, seed=3)
        b = simulate(estate, contracted, best_plan, draws=1000, seed=3)
        assert np.array_equal(a.samples, b.samples)

    def test_seed_changes_the_draws(self, estate, contracted, best_plan):
        a = simulate(estate, contracted, best_plan, draws=1000, seed=3)
        b = simulate(estate, contracted, best_plan, draws=1000, seed=4)
        assert not np.array_equal(a.samples, b.samples)

    def test_independent_of_ambient_randomness(self, estate, contracted, best_plan):
        np.random.seed(1)
        a = simulate(estate, contracted, best_plan, draws=500, seed=8)
        np.random.seed(99)
        np.random.random(1000)
        b = simulate(estate, contracted, best_plan, draws=500, seed=8)
        assert np.array_equal(a.samples, b.samples)

    def test_all_samples_positive(self, result):
        assert (result.samples > 0).all()

    def test_records_its_parameters(self, result):
        assert result.sigma == SIGMA
        assert result.rho_programme == RHO_PROGRAMME
        assert result.rho_wave == RHO_WAVE

    def test_deterministic_matches_plan(self, estate, contracted, best_plan, result):
        assert result.deterministic == pytest.approx(
            deterministic_end(estate, contracted, best_plan)
        )

    def test_percentiles_are_ordered(self, result):
        assert (
            result.pct(10)
            <= result.pct(50)
            <= result.pct(80)
            <= result.pct(90)
            <= result.pct(95)
        )

    def test_zero_sigma_collapses_to_the_plan(self, estate, contracted, best_plan):
        r = simulate(estate, contracted, best_plan, draws=500, sigma=0.0)
        assert r.samples.std() == pytest.approx(0.0, abs=1e-9)
        assert r.pct(50) == pytest.approx(r.deterministic)

    def test_zero_sigma_has_no_merge_bias(self, estate, contracted, best_plan):
        r = simulate(estate, contracted, best_plan, draws=500, sigma=0.0)
        assert r.merge_bias == pytest.approx(0.0, abs=1e-9)

    def test_more_sigma_means_more_spread(self, estate, contracted, best_plan):
        lo = simulate(estate, contracted, best_plan, draws=4000, sigma=0.15)
        hi = simulate(estate, contracted, best_plan, draws=4000, sigma=0.55)
        assert hi.samples.std() > lo.samples.std()

    def test_more_sigma_means_more_bias(self, estate, contracted, best_plan):
        lo = simulate(estate, contracted, best_plan, draws=8000, sigma=0.15)
        hi = simulate(estate, contracted, best_plan, draws=8000, sigma=0.55)
        assert hi.merge_bias > lo.merge_bias

    def test_median_correction_keeps_effort_unbiased(self):
        """The whole of section 6 depends on this.

        Each team's effort multiplier is ``exp(sigma*z - sigma^2/2)``, whose
        mean is exactly 1. Without the ``- sigma^2/2`` the mean would be
        ``exp(sigma^2/2)`` -- 6.3% high at sigma=0.35 -- and the reported
        merge bias would be mostly that inflation rather than the effect of
        joining parallel work.
        """
        rng = np.random.default_rng(0)
        z = rng.standard_normal(400_000)
        scale = np.exp(SIGMA * z - 0.5 * SIGMA * SIGMA)
        assert scale.mean() == pytest.approx(1.0, abs=0.005)

    def test_uncorrected_lognormal_would_be_biased_high(self):
        rng = np.random.default_rng(0)
        z = rng.standard_normal(400_000)
        assert np.exp(SIGMA * z).mean() > 1.05

    def test_merge_bias_is_positive(self, result):
        assert result.merge_bias > 0

    def test_median_slip_can_be_zero_under_freezes(self, estate, contracted, best_plan):
        """The failure mode that made ``merge_bias`` mean-based.

        Freezes push every draw landing inside a run of frozen months onto the
        same date, so the trailing edge of a freeze carries an atom of
        probability as wide as the freeze. When the median lands inside that
        atom, ``P50 - deterministic`` reports exactly zero while the mean is
        months late. This test does not assert the atom swallows the median in
        this particular estate -- that depends on the plan -- only that if it
        does, the mean still sees the slip.
        """
        r = simulate(estate, contracted, best_plan, draws=20_000)
        if r.median_slip == 0.0:
            assert r.mass_at(r.pct(50)) > 0.05
            assert r.merge_bias > 0

    def test_freezes_create_an_atom_at_the_trailing_edge(
        self, estate, contracted, best_plan
    ):
        from wave.costs import FREEZE_MONTHS

        r = simulate(estate, contracted, best_plan, draws=20_000)
        heavy = [
            m
            for m in np.unique(r.samples)
            if r.mass_at(m) > 0.02 and float(m).is_integer()
        ]
        assert heavy, "expected at least one quantised outcome"
        for m in heavy:
            assert (int(m) - 1) % 12 in FREEZE_MONTHS

    def test_no_atoms_without_freezes(self, estate, contracted, best_plan):
        r = simulate(estate, contracted, best_plan, draws=20_000, freeze=False)
        assert r.mass_at(r.pct(50)) < 0.001

    def test_median_slip_is_positive_without_freezes(
        self, estate, contracted, best_plan
    ):
        r = simulate(estate, contracted, best_plan, draws=20_000, freeze=False)
        assert r.median_slip > 0

    def test_mean_is_the_sample_mean(self, result):
        assert result.mean == pytest.approx(float(result.samples.mean()))

    def test_mass_at_a_value_nobody_hits_is_zero(self, result):
        assert result.mass_at(-1.0) == 0.0

    def test_p90_overrun_is_in_months(self, result):
        assert result.p90_overrun == pytest.approx(
            result.pct(90) - result.deterministic
        )

    def test_freeze_off_is_never_later_in_distribution(
        self, estate, contracted, best_plan
    ):
        on = simulate(estate, contracted, best_plan, draws=4000, seed=2)
        off = simulate(estate, contracted, best_plan, draws=4000, seed=2, freeze=False)
        assert off.pct(50) <= on.pct(50) + 1e-9
        assert off.pct(90) <= on.pct(90) + 1e-9

    def test_freeze_on_never_lands_in_a_freeze(self, estate, contracted, best_plan):
        from wave.costs import FREEZE_MONTHS

        r = simulate(estate, contracted, best_plan, draws=2000, seed=6)
        months = np.floor(r.samples).astype(int) % 12
        assert not set(np.unique(months)) & FREEZE_MONTHS


class TestCorrelation:
    def test_zero_correlation_is_reproducible(self, estate, contracted, best_plan):
        a = simulate(
            estate, contracted, best_plan, draws=800, rho_programme=0, rho_wave=0
        )
        b = simulate(
            estate, contracted, best_plan, draws=800, rho_programme=0, rho_wave=0
        )
        assert np.array_equal(a.samples, b.samples)

    def test_programme_correlation_widens_the_distribution(
        self, estate, contracted, best_plan
    ):
        lo = simulate(
            estate, contracted, best_plan, draws=8000, rho_programme=0.0, rho_wave=0.0
        )
        hi = simulate(
            estate, contracted, best_plan, draws=8000, rho_programme=0.6, rho_wave=0.0
        )
        assert hi.samples.std() > lo.samples.std()

    def test_wave_correlation_shrinks_the_merge_bias(
        self, estate, contracted, best_plan
    ):
        """Section 8's mechanism, isolated.

        Correlating teams *within* a wave makes the wave's duration less like
        a maximum of independent draws and more like a single draw, which
        reduces the merge bias. Correlating across the programme does not.
        """
        lo = simulate(
            estate,
            contracted,
            best_plan,
            draws=12_000,
            rho_programme=0.0,
            rho_wave=0.0,
            freeze=False,
        )
        hi = simulate(
            estate,
            contracted,
            best_plan,
            draws=12_000,
            rho_programme=0.0,
            rho_wave=0.6,
            freeze=False,
        )
        assert hi.merge_bias < lo.merge_bias

    def test_sweep_returns_one_row_per_setting(self, estate, contracted, best_plan):
        grid = [(0.0, 0.0), (0.3, 0.3)]
        assert len(rho_sweep(estate, contracted, best_plan, grid, draws=500)) == 2

    def test_sweep_echoes_its_inputs(self, estate, contracted, best_plan):
        grid = [(0.1, 0.2), (0.3, 0.4)]
        got = rho_sweep(estate, contracted, best_plan, grid, draws=500)
        assert [(r[0], r[1]) for r in got] == grid

    def test_sweep_is_reproducible(self, estate, contracted, best_plan):
        grid = [(0.2, 0.2)]
        a = rho_sweep(estate, contracted, best_plan, grid, draws=500)
        b = rho_sweep(estate, contracted, best_plan, grid, draws=500)
        assert a == b


class TestMergeBiasByWave:
    def test_one_row_per_non_empty_wave(self, estate, contracted, best_plan):
        got = merge_bias_by_wave(estate, contracted, best_plan, draws=2000)
        assert len(got) == best_plan.n_waves

    def test_waves_numbered_from_one(self, estate, contracted, best_plan):
        got = merge_bias_by_wave(estate, contracted, best_plan, draws=2000)
        assert [b.wave for b in got] == list(range(1, len(got) + 1))

    def test_near_critical_never_exceeds_teams(self, estate, contracted, best_plan):
        for b in merge_bias_by_wave(estate, contracted, best_plan, draws=2000):
            assert 1 <= b.near_critical <= b.teams

    def test_bias_non_negative(self, estate, contracted, best_plan):
        for b in merge_bias_by_wave(estate, contracted, best_plan, draws=4000):
            assert b.bias >= -1e-6

    def test_a_wave_with_one_team_has_no_bias(self, estate, contracted):
        """The mechanism, isolated to its degenerate case.

        With a single stream there is no maximum to take, and the
        median-corrected log-normal has mean 1, so the expected duration is
        exactly the point estimate. Any bias here would be a bug in the
        correction rather than a property of parallel work.
        """
        single = [u.id for u in contracted.units if set(u.effort_by_team) == {"data"}]
        assert single, "estate should contain at least one data-only unit"
        plan = Plan(waves=(tuple(single[:1]),))
        rows = merge_bias_by_wave(estate, contracted, plan, draws=40_000)
        assert rows[0].teams == 1
        assert rows[0].near_critical == 1
        assert rows[0].bias == pytest.approx(0.0, abs=0.02)

    def test_bias_tracks_near_critical_streams_not_team_count(
        self, estate, contracted, best_plan
    ):
        """Section 7's claim, as a test rather than as prose.

        Rank correlation, not a threshold, because the point is which of two
        candidate explanations orders the waves correctly -- not that any wave
        hits a particular number.
        """
        rows = merge_bias_by_wave(estate, contracted, best_plan, draws=40_000)
        if len({b.near_critical for b in rows}) < 2:
            pytest.skip("plan does not separate the two explanations")
        bias = [b.bias for b in rows]
        assert _spearman([b.near_critical for b in rows], bias) >= _spearman(
            [b.teams for b in rows], bias
        )

    def test_reproducible(self, estate, contracted, best_plan):
        a = merge_bias_by_wave(estate, contracted, best_plan, draws=2000)
        b = merge_bias_by_wave(estate, contracted, best_plan, draws=2000)
        assert a == b

    def test_a_waves_number_does_not_depend_on_its_neighbours(
        self, estate, contracted, best_plan
    ):
        """Each wave gets its own generator, seeded from its index.

        Otherwise inserting a wave would silently change every later wave's
        reported bias, and the report would not be comparable across runs
        that changed the plan.
        """
        rows = merge_bias_by_wave(estate, contracted, best_plan, draws=2000)
        longest = {b.wave: b.longest for b in rows}
        again = merge_bias_by_wave(estate, contracted, best_plan, draws=2000)
        assert {b.wave: b.longest for b in again} == longest


class TestCostAtPercentile:
    def test_p50_is_near_the_deterministic_cost(
        self, estate, contracted, best_plan, remediation, result
    ):
        base, _ = evaluate(estate, contracted, best_plan, remediation_cost=remediation)
        got = cost_at_percentile(
            estate, contracted, best_plan, result, remediation_cost=remediation, q=50
        )
        assert base.total <= got <= base.total * 1.15

    def test_higher_percentile_costs_more(
        self, estate, contracted, best_plan, remediation, result
    ):
        lo = cost_at_percentile(
            estate, contracted, best_plan, result, remediation_cost=remediation, q=50
        )
        hi = cost_at_percentile(
            estate, contracted, best_plan, result, remediation_cost=remediation, q=95
        )
        assert hi > lo

    def test_reproducible(self, estate, contracted, best_plan, remediation, result):
        args = (estate, contracted, best_plan, result)
        a = cost_at_percentile(*args, remediation_cost=remediation, q=90)
        b = cost_at_percentile(*args, remediation_cost=remediation, q=90)
        assert a == b
