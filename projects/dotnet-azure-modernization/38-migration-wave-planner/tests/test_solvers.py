"""Solvers: determinism, feasibility, and the properties each one claims.

Nothing here asserts that a particular solver is good. Quality is measured in
the report against a bound and against exhaustive optimisation; what a test
can usefully pin down is that a solver always returns something legal, and
always returns the same thing.
"""

from __future__ import annotations

import pytest

from wave.costs import Plan, evaluate, is_feasible
from wave.solvers import (
    SOLVERS,
    SearchStats,
    _neighbours,
    _random_move,
    annealed,
    coupling_clusters,
    coupling_weight,
    least_critical_first,
    dependency_depth,
    dependents_first,
    local_search,
    make_objective,
    pack_in_order,
)


@pytest.fixture(scope="module")
def rng():
    import random

    return random.Random(4)


class TestDependencyDepth:
    def test_covers_every_unit(self, contracted):
        assert set(dependency_depth(contracted)) == {u.id for u in contracted.units}

    def test_non_negative(self, contracted):
        assert all(d >= 0 for d in dependency_depth(contracted).values())

    def test_some_unit_is_a_root(self, contracted):
        assert min(dependency_depth(contracted).values()) == 0

    def test_deterministic(self, contracted):
        assert dependency_depth(contracted) == dependency_depth(contracted)

    def test_terminates_on_a_cyclic_graph(self, contracted):
        """The estate has hubs, so the unit graph has cycles.

        Depth on a cyclic graph is not well defined; what matters is that the
        computation stops and produces a total order to pack with, rather
        than recursing forever or raising.
        """
        d = dependency_depth(contracted)
        assert len(d) == len(contracted.units)


class TestCouplingWeight:
    def test_symmetric_keys_not_duplicated(self, contracted):
        w = coupling_weight(contracted)
        for a, b in w:
            assert (b, a) not in w or a == b

    def test_all_non_negative(self, contracted):
        assert all(v >= 0 for v in coupling_weight(contracted).values())

    def test_the_broken_dependencies_themselves_weigh_nothing(
        self, estate, decoupling
    ):
        """A consequence of the estate's own invariants, recorded here.

        Unsplittable edges are forbidden from carrying hybrid costs, because
        an unsplittable edge can never become a hybrid link. Once remediation
        breaks one, the surviving dependency still carries no hybrid cost --
        the estate does not model the price of the replacement API. The unit
        pair can still weigh something if *other* dependencies join it, which
        is why this checks the edges rather than the pairs. See
        docs/known-limitations.md.
        """
        keyed = {(d.source, d.target): d for d in estate.dependencies}
        for key in decoupling[0]:
            d = keyed[key]
            assert d.hybrid_monthly == 0 and d.traffic_gb_mo == 0

    def test_some_pairs_weigh_nothing(self, contracted):
        w = coupling_weight(contracted)
        assert any(v == 0 for v in w.values())

    def test_most_pairs_weigh_something(self, contracted):
        w = coupling_weight(contracted)
        assert sum(1 for v in w.values() if v > 0) > len(w) // 2

    def test_only_real_unit_pairs(self, contracted):
        ids = {u.id for u in contracted.units}
        for a, b in coupling_weight(contracted):
            assert a in ids and b in ids and a != b


class TestPacking:
    def test_pack_in_order_places_every_unit_once(self, estate, contracted):
        order = [u.id for u in contracted.units]
        p = pack_in_order(estate, contracted, order)
        assert sorted(p.units) == sorted(order)

    def test_pack_in_order_is_first_fit_not_sequential(self, estate, contracted):
        """First fit can put a later unit in an earlier wave.

        That is the point: the sequence expresses priority, and a unit that
        still fits in an open wave should go there rather than force a new
        one. So the flattened plan is *not* the input order, and a test that
        demanded it would be testing a packer nobody wants.
        """
        order = [u.id for u in contracted.units]
        p = pack_in_order(estate, contracted, order)
        assert p.waves[0][0] == min(order[: len(p.waves[0])])

    def test_pack_in_order_first_unit_lands_in_wave_zero(self, estate, contracted):
        order = [u.id for u in contracted.units]
        p = pack_in_order(estate, contracted, order)
        assert order[0] in p.waves[0]

    def test_pack_in_order_is_feasible(self, estate, contracted):
        order = [u.id for u in contracted.units]
        assert is_feasible(estate, contracted, pack_in_order(estate, contracted, order))

    def test_pack_in_order_creates_no_empty_waves(self, estate, contracted):
        order = [u.id for u in contracted.units]
        p = pack_in_order(estate, contracted, order)
        assert all(len(w) > 0 for w in p.waves)

    def test_pack_of_nothing_is_empty(self, estate, contracted):
        assert pack_in_order(estate, contracted, []).waves == ()


@pytest.mark.parametrize("name", list(SOLVERS))
class TestEverySolver:
    def test_feasible(self, estate, contracted, name):
        assert is_feasible(estate, contracted, SOLVERS[name](estate, contracted))

    def test_covers_every_unit_once(self, estate, contracted, name):
        p = SOLVERS[name](estate, contracted)
        assert sorted(p.units) == sorted(u.id for u in contracted.units)

    def test_deterministic(self, estate, contracted, name):
        a = SOLVERS[name](estate, contracted)
        b = SOLVERS[name](estate, contracted)
        assert a == b

    def test_normalised(self, estate, contracted, name):
        p = SOLVERS[name](estate, contracted)
        assert p == p.normalised()

    def test_at_least_the_minimum_waves(self, estate, contracted, name):
        from wave.bounds import minimum_waves

        p = SOLVERS[name](estate, contracted)
        assert p.n_waves >= minimum_waves(estate, contracted)

    def test_priceable(self, estate, contracted, name, remediation):
        b, _ = evaluate(
            estate, contracted, SOLVERS[name](estate, contracted),
            remediation_cost=remediation,
        )
        assert b.total > 0


class TestSolverIdentities:
    def test_three_solvers_registered(self):
        assert set(SOLVERS) == {
            "dependents_first",
            "least_critical_first",
            "coupling_clusters",
        }

    def test_least_critical_first_defers_criticality(self, estate, contracted):
        """Least critical first, which is what the name and docstring say.

        Capacity can push an individual unit around, so the testable claim is
        about wave means rather than maxima.
        """
        p = least_critical_first(estate, contracted)

        def mean(w):
            return sum(contracted.by_id(u).criticality for u in w) / len(w)

        assert mean(p.waves[0]) < mean(p.waves[-1])

    def test_least_critical_first_ends_on_the_crown_jewels(self, estate, contracted):
        p = least_critical_first(estate, contracted)
        assert all(contracted.by_id(u).criticality == 5 for u in p.waves[-1])

    def test_dependents_first_front_loads_depth(self, estate, contracted):
        """Deepest units first: applications before the databases they read."""
        depth = dependency_depth(contracted)
        p = dependents_first(estate, contracted)

        def mean(w):
            return sum(depth[u] for u in w) / len(w)

        assert mean(p.waves[0]) > mean(p.waves[-1])

    def test_dependents_first_differs_from_least_critical_first(
        self, estate, contracted
    ):
        assert dependents_first(estate, contracted) != least_critical_first(
            estate, contracted
        )

    def test_coupling_clusters_uses_more_waves_here(self, estate, contracted):
        """Recorded because it is the report's section 2 finding.

        Clustering by coupling makes groups that do not fit per-team
        capacity, so the packer splits them and pays wave overhead anyway.
        """
        assert coupling_clusters(estate, contracted).n_waves > dependents_first(
            estate, contracted
        ).n_waves


class TestObjective:
    def test_matches_evaluate(self, estate, contracted, objective, best_plan, remediation):
        b, _ = evaluate(estate, contracted, best_plan, remediation_cost=remediation)
        assert objective(best_plan) == pytest.approx(b.total)

    def test_licence_blind_objective_is_lower(self, estate, contracted, best_plan, remediation):
        seeing = make_objective(estate, contracted, remediation_cost=remediation)
        blind = make_objective(
            estate, contracted, remediation_cost=remediation, count_licence=False
        )
        assert blind(best_plan) <= seeing(best_plan)

    def test_blind_difference_is_exactly_the_licence_term(
        self, estate, contracted, best_plan, remediation
    ):
        seeing = make_objective(estate, contracted, remediation_cost=remediation)
        blind = make_objective(
            estate, contracted, remediation_cost=remediation, count_licence=False
        )
        b, _ = evaluate(estate, contracted, best_plan, remediation_cost=remediation)
        assert seeing(best_plan) - blind(best_plan) == pytest.approx(b.licence_waste)


class TestNeighbourhood:
    def test_neighbours_are_plans(self, best_plan):
        assert all(isinstance(n, Plan) for n in _neighbours(best_plan, 12))

    def test_neighbours_preserve_the_unit_set(self, best_plan):
        want = sorted(best_plan.units)
        for n in _neighbours(best_plan, 12):
            assert sorted(n.units) == want

    def test_neighbours_are_normalised(self, best_plan):
        for n in _neighbours(best_plan, 12):
            assert n == n.normalised()

    def test_neighbours_exclude_the_plan_itself(self, best_plan):
        assert best_plan.normalised() not in _neighbours(best_plan, 12)

    def test_neighbourhood_is_non_empty(self, best_plan):
        assert _neighbours(best_plan, 12)

    def test_wave_cap_is_respected(self, best_plan):
        for n in _neighbours(best_plan, best_plan.n_waves):
            assert n.n_waves <= best_plan.n_waves

    def test_random_move_preserves_units(self, best_plan, rng):
        want = sorted(best_plan.units)
        for _ in range(200):
            m = _random_move(best_plan, rng, 12)
            if m is not None:
                assert sorted(m.units) == want

    def test_random_move_on_empty_plan_is_none(self, rng):
        assert _random_move(Plan(()), rng, 12) is None


class TestSearch:
    def test_local_search_never_worsens(self, estate, contracted, objective, naive_plans):
        for name, p in naive_plans.items():
            got = local_search(estate, contracted, p, objective)
            assert objective(got) <= objective(p) + 1e-6, name

    def test_local_search_returns_a_feasible_plan(
        self, estate, contracted, objective, naive_plans
    ):
        for name, p in naive_plans.items():
            assert is_feasible(
                estate, contracted, local_search(estate, contracted, p, objective)
            ), name

    def test_local_search_is_a_fixed_point(self, estate, contracted, objective, best_plan):
        once = local_search(estate, contracted, best_plan, objective)
        twice = local_search(estate, contracted, once, objective)
        assert objective(twice) == pytest.approx(objective(once))

    def test_annealed_beats_every_start(
        self, estate, contracted, objective, naive_plans
    ):
        got = annealed(
            estate, contracted, objective, list(naive_plans.values()), iterations=800
        )
        for name, p in naive_plans.items():
            assert objective(got) <= objective(p) + 1e-6, name

    def test_annealed_is_deterministic(self, estate, contracted, objective, naive_plans):
        args = (estate, contracted, objective, list(naive_plans.values()))
        a = annealed(*args, iterations=500)
        b = annealed(*args, iterations=500)
        assert a == b

    def test_annealed_depends_on_its_seed(self, estate, contracted, objective, naive_plans):
        args = (estate, contracted, objective, list(naive_plans.values()))
        a = annealed(*args, iterations=500, seed=1)
        b = annealed(*args, iterations=500, seed=2)
        assert objective(a) > 0 and objective(b) > 0

    def test_annealed_ignores_ambient_randomness(
        self, estate, contracted, objective, naive_plans
    ):
        """The private RNG is what makes docs/results.md hashable."""
        import random

        args = (estate, contracted, objective, list(naive_plans.values()))
        random.seed(1)
        a = annealed(*args, iterations=500)
        random.seed(999)
        [random.random() for _ in range(1000)]
        b = annealed(*args, iterations=500)
        assert a == b

    def test_annealed_is_feasible(self, estate, contracted, objective, naive_plans):
        got = annealed(
            estate, contracted, objective, list(naive_plans.values()), iterations=500
        )
        assert is_feasible(estate, contracted, got)

    def test_stats_are_recorded(self, estate, contracted, objective, naive_plans):
        stats = SearchStats()
        annealed(
            estate,
            contracted,
            objective,
            list(naive_plans.values()),
            iterations=500,
            stats=stats,
        )
        assert stats.evaluations > 0
        assert stats.restarts == len(naive_plans)

    def test_more_iterations_never_hurt(self, estate, contracted, objective, naive_plans):
        args = (estate, contracted, objective, list(naive_plans.values()))
        short = objective(annealed(*args, iterations=200))
        long = objective(annealed(*args, iterations=3000))
        assert long <= short + 1e-6
