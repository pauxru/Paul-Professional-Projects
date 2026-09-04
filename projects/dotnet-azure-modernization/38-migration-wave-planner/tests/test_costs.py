"""The cost model, the schedule, and the closed forms behind them.

Where a quantity has a closed form -- licence waste, freeze slippage, present
value -- the test states the closed form independently rather than calling
the implementation twice.
"""

from __future__ import annotations

import math

import pytest

from wave.costs import (
    DISCOUNT_ANNUAL,
    EGRESS_PER_GB,
    FREEZE_MONTHS,
    HORIZON_MONTHS,
    MAX_WAVE_MONTHS,
    WAVE_OVERHEAD,
    CostBreakdown,
    Plan,
    _out_of_freeze,
    cost_floor,
    do_nothing_cost,
    evaluate,
    is_feasible,
    licence_waste,
    payback_month,
    present_value,
    schedule,
    wave_duration,
)


class TestPlan:
    def test_n_waves(self):
        assert Plan((("a",), ("b", "c"))).n_waves == 2

    def test_units_flattens(self):
        assert Plan((("a",), ("b", "c"))).units == ("a", "b", "c")

    def test_wave_of(self):
        assert Plan((("a",), ("b", "c"))).wave_of() == {"a": 0, "b": 1, "c": 1}

    def test_normalised_sorts_within_wave(self):
        assert Plan((("c", "a"),)).normalised().waves == (("a", "c"),)

    def test_normalised_keeps_wave_order(self):
        p = Plan((("z",), ("a",))).normalised()
        assert p.waves == (("z",), ("a",))

    def test_normalised_drops_empty_waves(self):
        assert Plan(((), ("a",), ())).normalised().waves == (("a",),)

    def test_normalised_is_idempotent(self):
        p = Plan((("c", "a"), (), ("b",)))
        assert p.normalised().normalised() == p.normalised()

    def test_frozen_is_hashable(self):
        assert {Plan((("a",),)), Plan((("a",),))} == {Plan((("a",),))}


class TestWaveDuration:
    def test_empty_wave_is_zero(self, estate, contracted):
        assert wave_duration(estate, contracted, ()) == 0.0

    def test_single_unit_is_effort_over_rate(self, estate, contracted):
        u = contracted.by_id("esb")
        team, eff = next(iter(u.effort_by_team.items()))
        assert wave_duration(estate, contracted, ("esb",)) == pytest.approx(
            eff / estate.rate[team]
        )

    def test_is_max_over_teams_not_sum(self, estate, contracted):
        ids = [u.id for u in contracted.units[:4]]
        per: dict[str, float] = {}
        for uid in ids:
            for t, e in contracted.by_id(uid).effort_by_team.items():
                per[t] = per.get(t, 0.0) + e
        expect = max(e / estate.rate[t] for t, e in per.items())
        assert wave_duration(estate, contracted, tuple(ids)) == pytest.approx(expect)

    def test_monotone_in_wave_contents(self, estate, contracted):
        a = ("esb",)
        b = ("esb", contracted.units[0].id)
        if contracted.units[0].id == "esb":
            b = ("esb", contracted.units[1].id)
        assert wave_duration(estate, contracted, b) >= wave_duration(
            estate, contracted, a
        )

    def test_order_within_wave_is_irrelevant(self, estate, contracted):
        ids = tuple(u.id for u in contracted.units[:3])
        assert wave_duration(estate, contracted, ids) == wave_duration(
            estate, contracted, tuple(reversed(ids))
        )


class TestFeasibility:
    def test_solver_plans_are_feasible(self, estate, contracted, all_plans):
        for name, p in all_plans.items():
            assert is_feasible(estate, contracted, p), name

    def test_every_unit_scheduled_once(self, contracted, all_plans):
        want = sorted(u.id for u in contracted.units)
        for name, p in all_plans.items():
            assert sorted(p.units) == want, name

    def test_all_units_in_one_wave_is_infeasible(self, estate, contracted):
        everything = Plan((tuple(u.id for u in contracted.units),))
        assert not is_feasible(estate, contracted, everything)

    def test_one_unit_per_wave_is_feasible(self, estate, contracted):
        singles = Plan(tuple((u.id,) for u in contracted.units))
        assert is_feasible(estate, contracted, singles)

    def test_wave_never_exceeds_governance_limit(self, estate, contracted, all_plans):
        for name, p in all_plans.items():
            for w in p.waves:
                assert wave_duration(estate, contracted, w) <= MAX_WAVE_MONTHS + 1e-9


class TestFreezeSlippage:
    """``_out_of_freeze`` returns the month a cutover actually lands in."""

    @pytest.mark.parametrize("m", [0.0, 1.0, 3.5, 8.9, 12.0, 13.0])
    def test_open_months_do_not_move(self, m):
        assert _out_of_freeze(m) == m

    @pytest.mark.parametrize("m", [2.0, 2.5, 10.0, 10.4, 11.99])
    def test_frozen_months_move(self, m):
        assert _out_of_freeze(m) > m

    def test_slips_to_the_next_open_month(self):
        assert _out_of_freeze(2.4) == pytest.approx(3.0)

    def test_slips_past_a_run_of_frozen_months(self):
        assert _out_of_freeze(10.2) == pytest.approx(12.0)

    def test_result_is_never_frozen(self):
        for i in range(0, 2400):
            assert math.floor(_out_of_freeze(i / 100)) % 12 not in FREEZE_MONTHS

    def test_never_moves_backwards(self):
        for i in range(0, 2400):
            m = i / 100
            assert _out_of_freeze(m) >= m

    def test_slip_is_bounded_by_freeze_run_length(self):
        """The longest frozen run is months 10 and 11, so at most 2 months."""
        for i in range(0, 2400):
            m = i / 100
            assert _out_of_freeze(m) - m <= 2.0 + 1e-9

    def test_idempotent(self):
        for i in range(0, 2400):
            once = _out_of_freeze(i / 100)
            assert _out_of_freeze(once) == once


class TestSchedule:
    def test_cutovers_are_increasing(self, best_schedule):
        cuts = best_schedule.cutovers
        assert list(cuts) == sorted(cuts)

    def test_end_is_last_cutover(self, best_schedule):
        assert best_schedule.end_month == pytest.approx(best_schedule.cutovers[-1])

    def test_no_cutover_lands_in_a_freeze(self, best_schedule):
        for t in best_schedule.cutovers:
            assert math.floor(t) % 12 not in FREEZE_MONTHS

    def test_cutover_of_unit_covers_every_unit(self, contracted, best_schedule):
        assert set(best_schedule.cutover_of_unit()) == {
            u.id for u in contracted.units
        }

    def test_units_in_a_wave_share_a_cutover(self, best_schedule):
        cut = best_schedule.cutover_of_unit()
        for i, w in enumerate(best_schedule.plan.waves):
            for uid in w:
                assert cut[uid] == pytest.approx(best_schedule.cutovers[i])

    def test_durations_match_wave_durations(self, estate, contracted, best_schedule):
        for i, w in enumerate(best_schedule.plan.waves):
            assert best_schedule.durations[i] == pytest.approx(
                wave_duration(estate, contracted, w)
            )

    def test_freeze_off_is_never_later(self, estate, contracted, best_plan):
        on = schedule(estate, contracted, best_plan, freeze=True)
        off = schedule(estate, contracted, best_plan, freeze=False)
        assert off.end_month <= on.end_month + 1e-9

    def test_freeze_off_records_no_slip(self, estate, contracted, best_plan):
        off = schedule(estate, contracted, best_plan, freeze=False)
        assert all(s == 0.0 for s in off.freeze_slip)

    def test_start_month_shifts_everything(self, estate, contracted, best_plan):
        a = schedule(estate, contracted, best_plan, freeze=False)
        b = schedule(estate, contracted, best_plan, start_month=3.0, freeze=False)
        assert b.end_month == pytest.approx(a.end_month + 3.0)


class TestLicenceWaste:
    def test_no_licence_no_waste(self):
        assert licence_waste(0.0, 3, 7.0) == 0.0

    def test_cutover_at_renewal_wastes_nothing(self):
        assert licence_waste(12_000.0, 3, 3.0) == 0.0

    def test_cutover_a_year_after_renewal_wastes_nothing(self):
        assert licence_waste(12_000.0, 3, 15.0) == 0.0

    def test_one_month_in_wastes_eleven_twelfths(self):
        assert licence_waste(12_000.0, 3, 4.0) == pytest.approx(11_000.0)

    def test_one_month_before_renewal_wastes_one_twelfth(self):
        assert licence_waste(12_000.0, 3, 14.0) == pytest.approx(1_000.0)

    @pytest.mark.parametrize("k", range(12))
    def test_closed_form(self, k):
        annual, renews = 60_000.0, 5
        cut = renews + k + 24
        remaining = (12 - k) % 12
        assert licence_waste(annual, renews, float(cut)) == pytest.approx(
            annual * remaining / 12
        )

    def test_never_negative(self):
        for i in range(0, 480):
            assert licence_waste(50_000.0, 7, i / 10) >= 0.0

    def test_never_exceeds_a_full_term(self):
        for i in range(0, 480):
            assert licence_waste(50_000.0, 7, i / 10) <= 50_000.0 + 1e-9

    def test_periodic_in_twelve_months(self):
        for i in range(0, 240):
            m = i / 10
            assert licence_waste(24_000.0, 1, m) == pytest.approx(
                licence_waste(24_000.0, 1, m + 12)
            )


class TestPresentValue:
    def test_month_zero_is_face_value(self):
        assert present_value(100.0, 0.0) == pytest.approx(100.0)

    def test_discounts_the_future(self):
        assert present_value(100.0, 12.0) < 100.0

    def test_one_year_matches_the_annual_rate(self):
        assert present_value(100.0, 12.0) == pytest.approx(
            100.0 / (1 + DISCOUNT_ANNUAL)
        )

    def test_zero_rate_is_no_discount(self):
        assert present_value(100.0, 36.0, annual_rate=0.0) == pytest.approx(100.0)

    def test_monotone_in_time(self):
        vals = [present_value(100.0, m) for m in range(0, 48)]
        assert vals == sorted(vals, reverse=True)


class TestBreakdown:
    def test_total_is_the_sum_of_items(self, estate, contracted, best_plan, remediation):
        b, _ = evaluate(estate, contracted, best_plan, remediation_cost=remediation)
        assert b.total == pytest.approx(sum(v for _, v in b.items()))

    def test_every_term_is_non_negative(self, estate, contracted, all_plans, remediation):
        for name, p in all_plans.items():
            b, _ = evaluate(estate, contracted, p, remediation_cost=remediation)
            for label, v in b.items():
                assert v >= 0.0, f"{name}/{label}"

    def test_items_covers_every_field(self):
        b = CostBreakdown(1, 2, 3, 4, 5, 6, 7, 8, 9)
        assert len(b.items()) == 9
        assert b.total == 45

    def test_remediation_is_passed_through(self, estate, contracted, best_plan):
        b, _ = evaluate(estate, contracted, best_plan, remediation_cost=12_345.0)
        assert b.remediation == 12_345.0

    def test_overhead_is_per_wave(self, estate, contracted, all_plans, remediation):
        for name, p in all_plans.items():
            b, _ = evaluate(estate, contracted, p, remediation_cost=remediation)
            assert b.overhead == pytest.approx(WAVE_OVERHEAD * p.n_waves), name

    def test_licence_can_be_switched_off(self, estate, contracted, best_plan, remediation):
        on, _ = evaluate(estate, contracted, best_plan, remediation_cost=remediation)
        off, _ = evaluate(
            estate,
            contracted,
            best_plan,
            remediation_cost=remediation,
            count_licence=False,
        )
        assert off.licence_waste == 0.0
        assert on.total - off.total == pytest.approx(on.licence_waste)

    def test_egress_is_priced_per_gb(self, estate, contracted, best_plan, remediation):
        b, _ = evaluate(estate, contracted, best_plan, remediation_cost=remediation)
        gb = b.egress / EGRESS_PER_GB
        assert gb > 0
        assert b.egress == pytest.approx(gb * EGRESS_PER_GB)

    def test_deterministic(self, estate, contracted, best_plan, remediation):
        a, _ = evaluate(estate, contracted, best_plan, remediation_cost=remediation)
        b, _ = evaluate(estate, contracted, best_plan, remediation_cost=remediation)
        assert a == b

    def test_longer_horizon_costs_more(self, estate, contracted, best_plan, remediation):
        short, _ = evaluate(
            estate, contracted, best_plan, remediation_cost=remediation, horizon=36.0
        )
        long, _ = evaluate(
            estate, contracted, best_plan, remediation_cost=remediation, horizon=60.0
        )
        assert long.total > short.total


class TestFloorAndPayback:
    def test_floor_is_below_every_plan(self, estate, contracted, all_plans, remediation):
        floor = cost_floor(estate, remediation)
        for name, p in all_plans.items():
            b, _ = evaluate(estate, contracted, p, remediation_cost=remediation)
            assert floor <= b.total, name

    def test_floor_includes_remediation(self, estate):
        assert cost_floor(estate, 1000.0) - cost_floor(estate, 0.0) == pytest.approx(
            1000.0
        )

    def test_floor_scales_with_horizon(self, estate):
        a = cost_floor(estate, 0.0, horizon=24.0)
        b = cost_floor(estate, 0.0, horizon=48.0)
        assert b == pytest.approx(2 * a)

    def test_do_nothing_is_onprem_plus_licences(self, estate):
        run = sum(c.onprem_monthly for c in estate.components) * HORIZON_MONTHS
        lic = sum(c.licence_annual for c in estate.components) * (HORIZON_MONTHS / 12)
        assert do_nothing_cost(estate) == pytest.approx(run + lic)

    def test_do_nothing_counts_licences_the_migration_stops_paying(self, estate):
        run = sum(c.onprem_monthly for c in estate.components) * HORIZON_MONTHS
        assert do_nothing_cost(estate) > run

    def test_do_nothing_scales_with_horizon(self, estate):
        assert do_nothing_cost(estate, 24.0) == pytest.approx(
            do_nothing_cost(estate, 48.0) / 2
        )

    def test_do_nothing_is_dearer_than_migrating(
        self, estate, contracted, best_plan, remediation
    ):
        b, _ = evaluate(estate, contracted, best_plan, remediation_cost=remediation)
        assert do_nothing_cost(estate) > b.total

    def test_payback_is_within_horizon(self, estate, contracted, best_plan, remediation):
        m = payback_month(estate, contracted, best_plan, remediation_cost=remediation)
        assert m is not None and 0 < m < HORIZON_MONTHS

    def test_payback_after_last_cutover(
        self, estate, contracted, best_plan, remediation, best_schedule
    ):
        m = payback_month(estate, contracted, best_plan, remediation_cost=remediation)
        assert m > best_schedule.cutovers[0]

    def test_huge_remediation_never_pays_back(self, estate, contracted, best_plan):
        assert (
            payback_month(
                estate, contracted, best_plan, remediation_cost=1e12, limit=60.0
            )
            is None
        )
