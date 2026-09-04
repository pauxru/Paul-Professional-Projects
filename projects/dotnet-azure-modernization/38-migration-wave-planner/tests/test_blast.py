"""Blast radius: who is exposed when a wave cuts over, and what that trades against.

The interesting tests here are the ones about the transitive closure. The
runtime dependency graph has cycles in it -- a message bus that everything
talks to, and a data warehouse that feeds a report that feeds an operational
screen -- so the naive "walk your dependents" implementation does not
terminate. Two of the tests below exist purely to pin that down.
"""

from __future__ import annotations

import dataclasses

import pytest

from wave.blast import (
    PROPAGATING,
    _dependents,
    exposure_of_plan,
    pareto,
    peak_exposure,
    peak_live_links,
    wave_exposures,
)
from wave.costs import Plan
from wave.estate import Dependency, Link


@pytest.fixture(scope="module")
def exposures(estate, contracted, best_plan):
    return wave_exposures(estate, contracted, best_plan)


class TestDependents:
    def test_one_entry_per_component(self, estate):
        assert set(_dependents(estate)) == {c.id for c in estate.components}

    def test_terminates_on_the_real_estate(self, estate):
        assert _dependents(estate)

    def test_nobody_depends_on_themselves(self, estate):
        for node, deps in _dependents(estate).items():
            assert node not in deps

    def test_direct_dependents_are_included(self, estate):
        dep = _dependents(estate)
        for d in estate.dependencies:
            if d.link in PROPAGATING:
                assert d.source in dep[d.target]

    def test_transitive_dependents_are_included(self, estate):
        """If A propagates from B and B from C, C's failure reaches A."""
        dep = _dependents(estate)
        prop = [d for d in estate.dependencies if d.link in PROPAGATING]
        pairs = {(d.source, d.target) for d in prop}
        for a, b in pairs:
            for b2, cc in pairs:
                if b == b2:
                    assert a in dep[cc]

    def test_non_propagating_links_do_not_spread(self, estate):
        """A nightly batch feed does not take its consumer down with it."""
        dep = _dependents(estate)
        for d in estate.dependencies:
            if d.link in PROPAGATING:
                continue
            only_link = all(
                o.link not in PROPAGATING
                for o in estate.dependencies
                if o.source == d.source and o.target == d.target
            )
            reachable_another_way = any(
                d.source in dep[o.target]
                for o in estate.dependencies
                if o.source == d.source and o.link in PROPAGATING
            )
            if only_link and not reachable_another_way:
                assert d.source not in dep[d.target]

    def test_a_cycle_does_not_hang(self, estate):
        """The guard, exercised deliberately rather than incidentally.

        Two components that call each other synchronously is a design smell
        and also a real thing that exists in estates of this age. The closure
        must terminate, must report each side as exposed to the other, and --
        the part that the first implementation got wrong -- must give the same
        answer for a node regardless of which node was walked first.
        """
        ids = [c.id for c in estate.components[:2]]
        looped = dataclasses.replace(
            estate,
            dependencies=estate.dependencies
            + (
                Dependency(
                    source=ids[0],
                    target=ids[1],
                    link=Link.SYNC,
                    traffic_gb_mo=1.0,
                    hybrid_build=1.0,
                    hybrid_monthly=1.0,
                ),
                Dependency(
                    source=ids[1],
                    target=ids[0],
                    link=Link.SYNC,
                    traffic_gb_mo=1.0,
                    hybrid_build=1.0,
                    hybrid_monthly=1.0,
                ),
            ),
        )
        dep = _dependents(looped)
        assert ids[1] in dep[ids[0]]
        assert ids[0] in dep[ids[1]]

    def test_the_closure_does_not_depend_on_component_order(self, estate):
        """The bug the cycle test found.

        A memoised walk that skips nodes already on the stack caches a
        truncated answer under the node alone. Reversing the order the outer
        loop visits components then changes the reported closure, which means
        the blast radius of a change depends on the order rows appear in a
        source file.
        """
        ids = [c.id for c in estate.components[:2]]
        extra = (
            Dependency(
                source=ids[0],
                target=ids[1],
                link=Link.SYNC,
                traffic_gb_mo=1.0,
                hybrid_build=1.0,
                hybrid_monthly=1.0,
            ),
            Dependency(
                source=ids[1],
                target=ids[0],
                link=Link.SYNC,
                traffic_gb_mo=1.0,
                hybrid_build=1.0,
                hybrid_monthly=1.0,
            ),
        )
        forward = dataclasses.replace(
            estate, dependencies=estate.dependencies + extra
        )
        reversed_ = dataclasses.replace(
            forward, components=tuple(reversed(forward.components))
        )
        assert _dependents(forward) == _dependents(reversed_)

    def test_the_closure_does_not_depend_on_dependency_order(self, estate):
        shuffled = dataclasses.replace(
            estate, dependencies=tuple(reversed(estate.dependencies))
        )
        assert _dependents(estate) == _dependents(shuffled)

    def test_memoised_result_is_the_same_as_a_fresh_one(self, estate):
        assert _dependents(estate) == _dependents(estate)


class TestWaveExposures:
    def test_one_row_per_wave(self, exposures, best_plan):
        assert len(exposures) == best_plan.n_waves

    def test_waves_numbered_from_one(self, exposures):
        assert [e.wave for e in exposures] == list(range(1, len(exposures) + 1))

    def test_every_component_moves_exactly_once(self, exposures, estate):
        moved = [m for e in exposures for m in e.moving]
        assert sorted(moved) == sorted(c.id for c in estate.components)

    def test_exposed_contains_moving(self, exposures):
        for e in exposures:
            assert set(e.moving) <= set(e.exposed)

    def test_exposed_is_sorted_and_unique(self, exposures):
        for e in exposures:
            assert list(e.exposed) == sorted(set(e.exposed))

    def test_size_is_the_exposed_count(self, exposures):
        for e in exposures:
            assert e.size == len(e.exposed)

    def test_weighted_is_summed_criticality(self, exposures, estate):
        for e in exposures:
            assert e.weighted == pytest.approx(
                sum(estate.by_id(x).criticality for x in e.exposed)
            )

    def test_weighted_is_at_least_the_size(self, exposures):
        """Criticality starts at 1, so weighting can only add."""
        for e in exposures:
            assert e.weighted >= e.size

    def test_live_links_are_zero_after_the_last_wave(self, exposures):
        """Nothing is half-migrated once everything has moved."""
        assert exposures[-1].live_links == 0

    def test_live_links_are_non_negative(self, exposures):
        assert all(e.live_links >= 0 for e in exposures)

    def test_a_single_wave_plan_never_opens_a_link(self, estate, contracted):
        big = Plan(waves=(tuple(u.id for u in contracted.units),))
        ex = wave_exposures(estate, contracted, big)
        assert peak_live_links(ex) == 0

    def test_a_single_wave_plan_exposes_everything_at_once(
        self, estate, contracted
    ):
        big = Plan(waves=(tuple(u.id for u in contracted.units),))
        ex = wave_exposures(estate, contracted, big)
        assert set(ex[0].exposed) == {c.id for c in estate.components}

    def test_deterministic(self, estate, contracted, best_plan):
        assert wave_exposures(estate, contracted, best_plan) == wave_exposures(
            estate, contracted, best_plan
        )


class TestPeaks:
    def test_peak_exposure_is_the_max(self, exposures):
        assert peak_exposure(exposures) == max(e.weighted for e in exposures)

    def test_peak_live_links_is_the_max(self, exposures):
        assert peak_live_links(exposures) == max(e.live_links for e in exposures)

    def test_empty_peaks_are_zero(self):
        assert peak_exposure([]) == 0.0
        assert peak_live_links([]) == 0

    def test_exposure_of_plan_agrees(self, estate, contracted, best_plan, exposures):
        assert exposure_of_plan(estate, contracted, best_plan) == (
            peak_exposure(exposures),
            peak_live_links(exposures),
        )


class TestPareto:
    def test_empty(self):
        assert pareto([]) == []

    def test_single_point_survives(self):
        assert pareto([(1.0, 2.0, "a")]) == [(1.0, 2.0, "a")]

    def test_dominated_point_is_dropped(self):
        pts = [(1.0, 1.0, "good"), (2.0, 2.0, "bad")]
        assert [p[2] for p in pareto(pts)] == ["good"]

    def test_a_trade_off_keeps_both(self):
        pts = [(1.0, 5.0, "cheap"), (5.0, 1.0, "safe")]
        assert {p[2] for p in pareto(pts)} == {"cheap", "safe"}

    def test_equal_on_one_axis_better_on_the_other_dominates(self):
        pts = [(1.0, 1.0, "a"), (1.0, 2.0, "b")]
        assert [p[2] for p in pareto(pts)] == ["a"]

    def test_output_is_sorted_cheapest_first(self):
        pts = [(9.0, 1.0, "c"), (1.0, 9.0, "a"), (5.0, 5.0, "b")]
        assert [p[0] for p in pareto(pts)] == [1.0, 5.0, 9.0]

    def test_duplicates_collapse(self):
        pts = [(1.0, 1.0, "a"), (1.0, 1.0, "b")]
        assert len(pareto(pts)) == 1

    def test_tied_points_do_not_annihilate_each_other(self):
        """The bug this frontier had.

        Under a non-strict dominance test each of two identical points
        dominates the other, so both are discarded and the frontier comes back
        empty -- silently, and only when two heuristics happen to agree.
        """
        assert pareto([(1.0, 1.0, "a"), (1.0, 1.0, "b")]) == [(1.0, 1.0, "a")]

    def test_a_tie_does_not_hide_a_genuine_frontier_point(self):
        pts = [(1.0, 5.0, "a"), (1.0, 5.0, "b"), (5.0, 1.0, "c")]
        assert {p[2] for p in pareto(pts)} == {"a", "c"}

    def test_tie_break_is_by_name_not_input_order(self):
        assert pareto([(1.0, 1.0, "z"), (1.0, 1.0, "a")]) == pareto(
            [(1.0, 1.0, "a"), (1.0, 1.0, "z")]
        )

    def test_idempotent(self):
        pts = [(1.0, 9.0, "a"), (5.0, 5.0, "b"), (9.0, 1.0, "c"), (6.0, 6.0, "d")]
        once = pareto(pts)
        assert pareto(once) == once

    def test_frontier_is_a_subset_of_the_input(self):
        pts = [(1.0, 9.0, "a"), (5.0, 5.0, "b"), (9.0, 1.0, "c"), (6.0, 6.0, "d")]
        assert set(pareto(pts)) <= set(pts)

    def test_frontier_is_monotone_decreasing_on_the_second_axis(self):
        pts = [(1.0, 9.0, "a"), (5.0, 5.0, "b"), (9.0, 1.0, "c"), (6.0, 6.0, "d")]
        ys = [p[1] for p in pareto(pts)]
        assert ys == sorted(ys, reverse=True)

    def test_real_plans_land_on_a_frontier(self, estate, contracted, all_plans):
        pts = [
            (float(i), *exposure_of_plan(estate, contracted, p)[:1], name)
            for i, (name, p) in enumerate(all_plans.items())
        ]
        pts = [(a, b, c) for a, b, c in pts]
        assert pareto(pts)
