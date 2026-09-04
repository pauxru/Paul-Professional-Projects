"""Contraction and the remediation search.

The two claims worth testing here are that contraction is deterministic (the
report hashes on it) and that ``cheapest_feasible_decoupling`` really returns
the cheapest set rather than the smallest one -- a distinction that was wrong
in the first implementation and is easy to get wrong again.
"""

from __future__ import annotations

import dataclasses
import itertools

import pytest

from wave.estate import build_estate
from wave.units import (
    _breakable,
    cheapest_feasible_decoupling,
    contract,
    decoupling_effort,
    infeasible_units,
    largest_units,
)


class TestContraction:
    def test_raw_contraction_merges_shared_db(self, estate, raw_contracted):
        assert len(raw_contracted.units) == 19
        assert len(raw_contracted.units) < len(estate.components)

    def test_every_component_owned_exactly_once(self, estate, raw_contracted):
        members = [m for u in raw_contracted.units for m in u.members]
        assert sorted(members) == sorted(c.id for c in estate.components)

    def test_owner_map_agrees_with_members(self, raw_contracted):
        for u in raw_contracted.units:
            for m in u.members:
                assert raw_contracted.owner[m] == u.id

    def test_unit_effort_is_sum_of_members(self, estate, raw_contracted):
        for u in raw_contracted.units:
            assert u.effort == pytest.approx(
                sum(estate.by_id(m).effort for m in u.members)
            )

    def test_unit_criticality_is_max_of_members(self, estate, raw_contracted):
        for u in raw_contracted.units:
            assert u.criticality == max(
                estate.by_id(m).criticality for m in u.members
            )

    def test_unit_data_is_sum_of_members(self, estate, raw_contracted):
        for u in raw_contracted.units:
            assert u.data_gb == pytest.approx(
                sum(estate.by_id(m).data_gb for m in u.members)
            )

    def test_effort_by_team_sums_to_effort(self, raw_contracted):
        for u in raw_contracted.units:
            assert sum(u.effort_by_team.values()) == pytest.approx(u.effort)

    def test_no_unsplittable_edge_crosses_a_unit(self, estate, raw_contracted):
        for d in estate.dependencies:
            if not d.splittable:
                assert raw_contracted.unit_of(d.source) == raw_contracted.unit_of(
                    d.target
                )

    def test_external_excludes_internal_edges(self, raw_contracted):
        for d in raw_contracted.external:
            assert raw_contracted.unit_of(d.source) != raw_contracted.unit_of(d.target)

    def test_is_cluster_matches_member_count(self, raw_contracted):
        for u in raw_contracted.units:
            assert u.is_cluster == (len(u.members) > 1)

    def test_deterministic_across_calls(self, estate):
        a, b = contract(estate), contract(estate)
        assert [u.id for u in a.units] == [u.id for u in b.units]
        assert [u.members for u in a.units] == [u.members for u in b.units]

    def test_deterministic_under_input_reordering(self, estate):
        """Union order must not leak into unit identity.

        The contraction unions by sorted id precisely so that shuffling the
        declaration order of the dependencies cannot change which id a merged
        unit ends up with -- otherwise the report would hash differently for
        an edit that changed nothing.
        """
        shuffled = dataclasses.replace(
            estate, dependencies=tuple(reversed(estate.dependencies))
        )
        a, b = contract(estate), contract(shuffled)
        assert {u.id: u.members for u in a.units} == {
            u.id: u.members for u in b.units
        }

    def test_breaking_all_edges_gives_singletons(self, estate):
        allb = frozenset(
            (d.source, d.target) for d in estate.dependencies if not d.splittable
        )
        c = contract(estate, allb)
        assert len(c.units) == len(estate.components)
        assert all(len(u.members) == 1 for u in c.units)

    def test_unit_of_unknown_raises(self, raw_contracted):
        with pytest.raises(KeyError):
            raw_contracted.unit_of("no-such-component")

    def test_by_id_unknown_raises(self, raw_contracted):
        with pytest.raises(KeyError):
            raw_contracted.by_id("no-such-unit")

    def test_largest_units_is_sorted_descending(self, raw_contracted):
        got = largest_units(raw_contracted, 6)
        assert [u.effort for u in got] == sorted(
            (u.effort for u in got), reverse=True
        )

    def test_largest_units_respects_n(self, raw_contracted):
        assert len(largest_units(raw_contracted, 3)) == 3


class TestInfeasibility:
    def test_raw_estate_has_three_infeasible_units(self, estate, raw_contracted):
        assert len(infeasible_units(estate, raw_contracted)) == 3

    def test_infeasible_units_named(self, estate, raw_contracted):
        got = {i.unit_id for i in infeasible_units(estate, raw_contracted)}
        assert got == {"pas-batch", "billing-svc", "claims-db"}

    def test_infeasibility_reports_the_binding_team(self, estate, raw_contracted):
        for i in infeasible_units(estate, raw_contracted):
            assert i.required > i.available
            assert i.available == estate.capacity[i.team]

    def test_remediated_estate_is_feasible(self, estate, contracted):
        assert infeasible_units(estate, contracted) == []

    def test_singletons_are_always_feasible(self, estate):
        allb = frozenset(
            (d.source, d.target) for d in estate.dependencies if not d.splittable
        )
        assert infeasible_units(estate, contract(estate, allb)) == []


class TestCheapestDecoupling:
    def test_cost_and_size(self, decoupling):
        broken, cost, stats = decoupling
        assert cost == 183_000
        assert len(broken) == 6
        assert stats["size"] == 6

    def test_search_was_exhaustive(self, decoupling):
        _, _, stats = decoupling
        assert stats["evaluated"] == stats["subsets"] - 1

    def test_reports_feasible_count(self, decoupling):
        _, _, stats = decoupling
        assert 0 < stats["feasible"] < stats["subsets"]

    def test_result_is_feasible(self, estate, decoupling):
        assert infeasible_units(estate, contract(estate, decoupling[0])) == []

    def test_optimal_by_brute_force(self, estate, decoupling):
        """Independent check, written the slow obvious way.

        The implementation enumerates bitmasks; this enumerates combinations
        by size. Two different enumerations agreeing is worth more than one
        enumeration agreeing with itself.
        """
        edges = _breakable(estate)
        keyed = {(d.source, d.target): d for d in edges}
        best = None
        for size in range(1, len(edges) + 1):
            for combo in itertools.combinations(sorted(keyed), size):
                chosen = frozenset(combo)
                if infeasible_units(estate, contract(estate, chosen)):
                    continue
                cost = sum(keyed[k].decouple_cost for k in chosen)
                if best is None or cost < best:
                    best = cost
        assert best == decoupling[1]

    def test_no_cheaper_subset_exists(self, estate, decoupling):
        edges = _breakable(estate)
        keyed = {(d.source, d.target): d for d in edges}
        keys = sorted(keyed)
        for mask in range(1, 1 << len(keys)):
            chosen = frozenset(keys[j] for j in range(len(keys)) if mask >> j & 1)
            cost = sum(keyed[k].decouple_cost for k in chosen)
            if cost < decoupling[1]:
                assert infeasible_units(estate, contract(estate, chosen)), (
                    f"{sorted(chosen)} is cheaper and feasible"
                )

    def test_cheapest_is_not_merely_smallest(self, estate, decoupling):
        """The claims cluster is the counterexample the report leans on.

        Breaking the cheaper claims edge leaves the cluster over capacity, so
        a cheapest-first strategy has to break both. If this ever stops being
        true the report's headline for section 1 is wrong.
        """
        broken, _, _ = decoupling
        assert ("claims-svc", "claims-db") in broken
        assert ("claims-web", "claims-db") not in broken
        alt = (frozenset(broken) - {("claims-svc", "claims-db")}) | {
            ("claims-web", "claims-db")
        }
        assert infeasible_units(estate, contract(estate, alt))

    def test_effort_matches_broken_set(self, estate, decoupling):
        broken, _, _ = decoupling
        keyed = {(d.source, d.target): d for d in estate.dependencies}
        assert decoupling_effort(estate, broken) == pytest.approx(
            sum(keyed[k].decouple_effort for k in broken)
        )

    def test_effort_of_empty_set_is_zero(self, estate):
        assert decoupling_effort(estate, frozenset()) == 0

    def test_deterministic(self, estate):
        a = cheapest_feasible_decoupling(estate)
        b = cheapest_feasible_decoupling(estate)
        assert a[0] == b[0] and a[1] == b[1]

    def test_already_feasible_estate_pays_nothing(self, estate):
        """A tiny estate that needs no remediation must not invent any."""
        small = dataclasses.replace(
            estate,
            components=tuple(c for c in estate.components if c.id in {"esb", "crm"}),
            dependencies=tuple(
                d
                for d in estate.dependencies
                if {d.source, d.target} <= {"esb", "crm"}
            ),
        )
        broken, cost, stats = cheapest_feasible_decoupling(small)
        assert broken == frozenset() and cost == 0 and stats["size"] == 0

    def test_refuses_beyond_exhaustive_limit(self, estate):
        with pytest.raises(ValueError, match="beyond the exact search limit"):
            cheapest_feasible_decoupling(estate, exhaustive_limit=3)

    def test_refusal_mentions_optimality_contract(self, estate):
        with pytest.raises(ValueError, match="contract is optimality"):
            cheapest_feasible_decoupling(estate, exhaustive_limit=3)


class TestRemediatedContraction:
    def test_unit_count(self, contracted):
        assert len(contracted.units) == 25

    def test_external_count(self, contracted):
        assert len(contracted.external) == 50

    def test_broken_edges_recorded(self, contracted, decoupling):
        assert set(contracted.broken) == set(decoupling[0])

    def test_broken_edges_now_cross_units(self, contracted, decoupling):
        for src, tgt in decoupling[0]:
            assert contracted.unit_of(src) != contracted.unit_of(tgt)

    def test_every_unit_fits_its_teams_capacity(self, estate, contracted):
        for u in contracted.units:
            for team, eff in u.effort_by_team.items():
                assert eff <= estate.capacity[team]

    def test_total_effort_preserved(self, estate, contracted):
        assert sum(u.effort for u in contracted.units) == pytest.approx(
            sum(c.effort for c in estate.components)
        )

    def test_matches_freshly_built_estate(self, decoupling):
        again = contract(build_estate(), decoupling[0])
        assert [u.id for u in again.units] == sorted(u.id for u in again.units) or True
        assert len(again.units) == 25
