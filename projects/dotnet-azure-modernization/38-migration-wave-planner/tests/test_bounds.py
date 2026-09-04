"""The lower bound and the exact solver.

The bound has exactly one contract: it must never exceed the cost of a
feasible plan. A bound that is merely usually right is worse than no bound,
because it converts "we might be leaving money on the table" into "we are
provably close" without earning it. So the bulk of this module is a
randomised search for a counterexample.
"""

from __future__ import annotations

import random

import pytest

from wave.bounds import (
    Bound,
    SubInstance,
    _prefix_cutover_bounds,
    connected_slice,
    exact_optimum,
    lower_bound,
    minimum_waves,
    restrict,
)
from wave.costs import MAX_WAVE_MONTHS, Plan, evaluate, is_feasible, wave_duration
from wave.solvers import SOLVERS, annealed, make_objective


def random_feasible_plans(estate, c, n, seed=0):
    """Random legal plans, built by shuffling and first-fitting.

    Deliberately not produced by any solver: the bound must hold for plans
    nobody would ever propose, and solver output is a biased sample of the
    feasible set.
    """
    rng = random.Random(seed)
    ids = [u.id for u in c.units]
    out = []
    while len(out) < n:
        rng.shuffle(ids)
        waves: list[list[str]] = [[]]
        for uid in ids:
            placed = False
            for w in waves:
                trial = w + [uid]
                if wave_duration(estate, c, tuple(trial)) <= MAX_WAVE_MONTHS + 1e-9:
                    w.append(uid)
                    placed = True
                    break
            if not placed:
                waves.append([uid])
        p = Plan(tuple(tuple(w) for w in waves)).normalised()
        if is_feasible(estate, c, p):
            out.append(p)
    return out


class TestMinimumWaves:
    def test_at_least_one(self, estate, contracted):
        assert minimum_waves(estate, contracted) >= 1

    def test_no_plan_uses_fewer(self, estate, contracted, all_plans):
        m = minimum_waves(estate, contracted)
        for name, p in all_plans.items():
            assert p.n_waves >= m, name

    def test_no_random_plan_uses_fewer(self, estate, contracted):
        m = minimum_waves(estate, contracted)
        for p in random_feasible_plans(estate, contracted, 40, seed=3):
            assert p.n_waves >= m

    def test_is_a_ceiling_of_the_team_ratio(self, estate, contracted):
        per: dict[str, float] = {}
        for u in contracted.units:
            for t, e in u.effort_by_team.items():
                per[t] = per.get(t, 0.0) + e
        import math

        want = max(math.ceil(e / estate.capacity[t] - 1e-9) for t, e in per.items())
        assert minimum_waves(estate, contracted) >= want

    def test_deterministic(self, estate, contracted):
        assert minimum_waves(estate, contracted) == minimum_waves(estate, contracted)


class TestPrefixBounds:
    @pytest.mark.parametrize("team", ["policy", "claims", "finance", "data"])
    def test_non_decreasing(self, estate, contracted, team):
        got = [t for _, t in _prefix_cutover_bounds(estate, contracted, team)]
        assert got == sorted(got)

    @pytest.mark.parametrize("team", ["policy", "claims", "finance", "data"])
    def test_covers_that_teams_units(self, estate, contracted, team):
        got = {u for u, _ in _prefix_cutover_bounds(estate, contracted, team)}
        want = {u.id for u in contracted.units if team in u.effort_by_team}
        assert got == want

    def test_earliest_is_positive(self, estate, contracted):
        for team in estate.rate:
            got = _prefix_cutover_bounds(estate, contracted, team)
            if got:
                assert got[0][1] > 0


class TestBoundValidity:
    def test_bound_is_below_every_solver_plan(
        self, estate, contracted, all_plans, remediation
    ):
        lb = lower_bound(estate, contracted, remediation_cost=remediation)
        for name, p in all_plans.items():
            b, _ = evaluate(estate, contracted, p, remediation_cost=remediation)
            assert lb.total <= b.total, name

    def test_bound_is_below_two_hundred_random_plans(
        self, estate, contracted, remediation
    ):
        lb = lower_bound(estate, contracted, remediation_cost=remediation)
        for p in random_feasible_plans(estate, contracted, 200, seed=11):
            b, _ = evaluate(estate, contracted, p, remediation_cost=remediation)
            assert lb.total <= b.total, p.waves

    def test_bound_is_below_the_exact_optimum_of_slices(self, estate, contracted):
        for n in (5, 6, 7):
            sub = connected_slice(contracted, n)
            se, sc = restrict(estate, contracted, set(sub.units))
            _, opt, _ = exact_optimum(se, sc, sub, remediation_cost=0.0)
            assert lower_bound(se, sc, remediation_cost=0.0).total <= opt + 1e-6

    def test_hybrid_build_bound_never_exceeds_achieved_build(
        self, estate, contracted, remediation
    ):
        """The term that was wrong once.

        The first implementation kept the largest-cardinality feasible set of
        neighbours rather than the highest-value one, which understated what
        a plan could avoid and so overstated the charge. Overstating any term
        can push the "lower" bound above a real plan's cost.
        """
        lb = lower_bound(estate, contracted, remediation_cost=remediation)
        for p in random_feasible_plans(estate, contracted, 60, seed=5):
            b, _ = evaluate(estate, contracted, p, remediation_cost=remediation)
            assert lb.hybrid_build <= b.hybrid_build + 1e-6

    def test_overhead_term_never_exceeds_achieved(
        self, estate, contracted, remediation
    ):
        lb = lower_bound(estate, contracted, remediation_cost=remediation)
        for p in random_feasible_plans(estate, contracted, 60, seed=7):
            b, _ = evaluate(estate, contracted, p, remediation_cost=remediation)
            assert lb.overhead <= b.overhead + 1e-6

    def test_run_term_never_exceeds_achieved(self, estate, contracted, remediation):
        lb = lower_bound(estate, contracted, remediation_cost=remediation)
        for p in random_feasible_plans(estate, contracted, 60, seed=9):
            b, _ = evaluate(estate, contracted, p, remediation_cost=remediation)
            assert lb.run <= b.onprem_run + b.cloud_run + 1e-6

    def test_remediation_passes_through(self, estate, contracted):
        assert lower_bound(estate, contracted, remediation_cost=999.0).remediation == 999

    def test_total_is_the_sum(self):
        b = Bound(overhead=1, run=2, dual=3, hybrid_build=4, remediation=5, min_waves=1)
        assert b.total == 15

    def test_bound_charges_nothing_for_licences(self, estate, contracted, remediation):
        """Recorded because the report calls this out as provable slack."""
        lb = lower_bound(estate, contracted, remediation_cost=remediation)
        assert lb.total == pytest.approx(
            lb.overhead + lb.run + lb.dual + lb.hybrid_build + lb.remediation
        )

    def test_deterministic(self, estate, contracted, remediation):
        a = lower_bound(estate, contracted, remediation_cost=remediation)
        b = lower_bound(estate, contracted, remediation_cost=remediation)
        assert a == b

    def test_scales_with_horizon(self, estate, contracted, remediation):
        short = lower_bound(
            estate, contracted, remediation_cost=remediation, horizon=24.0
        )
        long = lower_bound(
            estate, contracted, remediation_cost=remediation, horizon=48.0
        )
        assert long.run > short.run


class TestSubInstances:
    @pytest.mark.parametrize("n", [3, 4, 5, 6, 7])
    def test_slice_has_the_requested_size(self, contracted, n):
        assert len(connected_slice(contracted, n).units) == n

    @pytest.mark.parametrize("n", [3, 4, 5, 6, 7])
    def test_slice_is_connected(self, contracted, n):
        sub = connected_slice(contracted, n)
        keep = set(sub.units)
        adj: dict[str, set[str]] = {u: set() for u in keep}
        for d in contracted.external:
            a, b = contracted.unit_of(d.source), contracted.unit_of(d.target)
            if a in keep and b in keep and a != b:
                adj[a].add(b)
                adj[b].add(a)
        seen = {sub.units[0]}
        stack = [sub.units[0]]
        while stack:
            for m in adj[stack.pop()]:
                if m not in seen:
                    seen.add(m)
                    stack.append(m)
        assert seen == keep

    def test_slice_is_deterministic(self, contracted):
        assert connected_slice(contracted, 6) == connected_slice(contracted, 6)

    def test_seed_unit_is_included(self, contracted):
        uid = contracted.units[3].id
        assert uid in connected_slice(contracted, 5, seed_unit=uid).units

    def test_search_space_formula(self):
        sub = SubInstance(units=("a", "b", "c"), max_waves=4)
        assert sub.search_space == 4**3

    def test_restrict_keeps_only_wanted_units(self, estate, contracted):
        keep = {u.id for u in contracted.units[:5]}
        _, sc = restrict(estate, contracted, keep)
        assert {u.id for u in sc.units} == keep

    def test_restrict_drops_dangling_dependencies(self, estate, contracted):
        keep = {u.id for u in contracted.units[:5]}
        _, sc = restrict(estate, contracted, keep)
        for d in sc.external:
            assert sc.unit_of(d.source) in keep and sc.unit_of(d.target) in keep

    def test_restrict_keeps_components_of_kept_units(self, estate, contracted):
        keep = {u.id for u in contracted.units[:5]}
        se, sc = restrict(estate, contracted, keep)
        want = {m for u in contracted.units if u.id in keep for m in u.members}
        assert {c.id for c in se.components} == want


class TestExactOptimum:
    @pytest.mark.parametrize("n", [4, 5, 6])
    def test_optimum_is_feasible(self, estate, contracted, n):
        sub = connected_slice(contracted, n)
        se, sc = restrict(estate, contracted, set(sub.units))
        plan, _, _ = exact_optimum(se, sc, sub, remediation_cost=0.0)
        assert is_feasible(se, sc, plan)

    @pytest.mark.parametrize("n", [4, 5, 6])
    def test_optimum_covers_every_unit(self, estate, contracted, n):
        sub = connected_slice(contracted, n)
        se, sc = restrict(estate, contracted, set(sub.units))
        plan, _, _ = exact_optimum(se, sc, sub, remediation_cost=0.0)
        assert sorted(plan.units) == sorted(sub.units)

    @pytest.mark.parametrize("n", [4, 5, 6])
    def test_no_random_plan_beats_the_optimum(self, estate, contracted, n):
        sub = connected_slice(contracted, n)
        se, sc = restrict(estate, contracted, set(sub.units))
        _, opt, _ = exact_optimum(se, sc, sub, remediation_cost=0.0)
        obj = make_objective(se, sc, remediation_cost=0.0)
        for p in random_feasible_plans(se, sc, 60, seed=n):
            assert obj(p) >= opt - 1e-6

    @pytest.mark.parametrize("n", [4, 5, 6])
    def test_no_solver_beats_the_optimum(self, estate, contracted, n):
        sub = connected_slice(contracted, n)
        se, sc = restrict(estate, contracted, set(sub.units))
        _, opt, _ = exact_optimum(se, sc, sub, remediation_cost=0.0)
        obj = make_objective(se, sc, remediation_cost=0.0)
        for name, f in SOLVERS.items():
            assert obj(f(se, sc)) >= opt - 1e-6, name

    @pytest.mark.parametrize("n", [5, 6])
    def test_annealer_matches_the_optimum(self, estate, contracted, n):
        """The report's section 3 headline, checked at small n."""
        sub = connected_slice(contracted, n)
        se, sc = restrict(estate, contracted, set(sub.units))
        _, opt, _ = exact_optimum(se, sc, sub, remediation_cost=0.0)
        obj = make_objective(se, sc, remediation_cost=0.0)
        got = annealed(
            se, sc, obj, [f(se, sc) for f in SOLVERS.values()], iterations=1500
        )
        assert obj(got) == pytest.approx(opt, rel=1e-9)

    def test_feasible_count_is_below_the_space(self, estate, contracted):
        sub = connected_slice(contracted, 5)
        se, sc = restrict(estate, contracted, set(sub.units))
        _, _, feasible = exact_optimum(se, sc, sub, remediation_cost=0.0)
        assert 0 < feasible < sub.search_space

    def test_refuses_a_space_it_cannot_enumerate(self, estate, contracted):
        sub = SubInstance(units=tuple(u.id for u in contracted.units), max_waves=12)
        with pytest.raises(ValueError, match="search space"):
            exact_optimum(estate, contracted, sub, remediation_cost=0.0, space_limit=10)

    def test_deterministic(self, estate, contracted):
        sub = connected_slice(contracted, 5)
        se, sc = restrict(estate, contracted, set(sub.units))
        a = exact_optimum(se, sc, sub, remediation_cost=0.0)
        b = exact_optimum(se, sc, sub, remediation_cost=0.0)
        assert a[0] == b[0] and a[1] == b[1] and a[2] == b[2]
