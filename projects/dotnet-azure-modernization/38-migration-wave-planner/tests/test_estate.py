"""The estate's invariants, and proof that each one actually fires.

An invariant that has never been seen to fail is a comment. Every check in
``assert_estate_is_consistent`` gets a test that breaks the estate in exactly
the way the check exists to catch, so a refactor that quietly drops a check
fails here rather than three modules downstream.
"""

from __future__ import annotations

import dataclasses

import pytest

from wave.estate import (
    Dependency,
    Estate,
    Kind,
    Link,
    UNSPLITTABLE,
    assert_estate_is_consistent,
    build_estate,
)


def mutate(e: Estate, cid: str, **kw) -> Estate:
    comps = tuple(
        dataclasses.replace(c, **kw) if c.id == cid else c for c in e.components
    )
    return dataclasses.replace(e, components=comps)


def mutate_dep(e: Estate, src: str, tgt: str, **kw) -> Estate:
    deps = tuple(
        dataclasses.replace(d, **kw) if (d.source, d.target) == (src, tgt) else d
        for d in e.dependencies
    )
    return dataclasses.replace(e, dependencies=deps)


class TestShape:
    def test_builds(self, estate):
        assert len(estate.components) == 29

    def test_dependency_count(self, estate):
        assert len(estate.dependencies) == 54

    def test_six_teams(self, estate):
        assert len(estate.rate) == 6

    def test_every_team_owns_something(self, estate):
        owned = {c.team for c in estate.components}
        assert owned == set(estate.rate)

    def test_ids_unique(self, estate):
        ids = [c.id for c in estate.components]
        assert len(ids) == len(set(ids))

    def test_by_id_round_trips(self, estate):
        for c in estate.components:
            assert estate.by_id(c.id) is c

    def test_by_id_rejects_unknown(self, estate):
        with pytest.raises(KeyError):
            estate.by_id("no-such-component")

    def test_every_kind_used(self, estate):
        used = {c.kind for c in estate.components}
        assert used == set(Kind)

    def test_every_link_used(self, estate):
        used = {d.link for d in estate.dependencies}
        assert used == set(Link)

    def test_graph_is_connected_ignoring_direction(self, estate):
        adj: dict[str, set[str]] = {c.id: set() for c in estate.components}
        for d in estate.dependencies:
            adj[d.source].add(d.target)
            adj[d.target].add(d.source)
        seen = {estate.components[0].id}
        stack = [estate.components[0].id]
        while stack:
            for n in adj[stack.pop()]:
                if n not in seen:
                    seen.add(n)
                    stack.append(n)
        assert seen == {c.id for c in estate.components}

    @pytest.mark.parametrize("team", ["policy", "claims", "finance", "platform"])
    def test_capacity_is_rate_times_wave(self, estate, team):
        assert estate.capacity[team] == pytest.approx(estate.rate[team] * 2.5)

    def test_floor_months_is_binding_team(self, estate):
        per_team: dict[str, float] = {}
        for c in estate.components:
            per_team[c.team] = per_team.get(c.team, 0.0) + c.effort
        expect = max(e / estate.rate[t] for t, e in per_team.items())
        assert estate.floor_months == pytest.approx(expect)

    def test_floor_months_below_horizon(self, estate):
        assert 0 < estate.floor_months < 48


class TestUnsplittable:
    def test_only_shared_db_is_unsplittable(self):
        assert UNSPLITTABLE == {Link.SHARED_DB}

    def test_unsplittable_edges_exist(self, estate):
        assert sum(1 for d in estate.dependencies if not d.splittable) == 10

    def test_splittable_is_derived_from_link(self, estate):
        for d in estate.dependencies:
            assert d.splittable == (d.link not in UNSPLITTABLE)

    def test_unsplittable_edges_all_priced(self, estate):
        for d in estate.dependencies:
            if not d.splittable:
                assert d.decouple_cost > 0 and d.decouple_effort > 0

    def test_splittable_edges_carry_no_decoupling_price(self, estate):
        for d in estate.dependencies:
            if d.splittable:
                assert d.decouple_cost == 0 and d.decouple_effort == 0


class TestInvariantsFire:
    """Each test breaks one rule and expects that rule's own complaint."""

    def test_duplicate_component_id(self, estate):
        bad = dataclasses.replace(
            estate, components=estate.components + (estate.components[0],)
        )
        with pytest.raises(ValueError, match="duplicate component id"):
            assert_estate_is_consistent(bad)

    def test_dependency_from_unknown(self, estate):
        bad = dataclasses.replace(
            estate,
            dependencies=estate.dependencies
            + (Dependency("ghost", "esb", Link.SYNC, 1.0, 1.0, 1.0),),
        )
        with pytest.raises(ValueError, match="unknown component 'ghost'"):
            assert_estate_is_consistent(bad)

    def test_dependency_to_unknown(self, estate):
        bad = dataclasses.replace(
            estate,
            dependencies=estate.dependencies
            + (Dependency("esb", "ghost", Link.SYNC, 1.0, 1.0, 1.0),),
        )
        with pytest.raises(ValueError, match="unknown component 'ghost'"):
            assert_estate_is_consistent(bad)

    def test_self_dependency(self, estate):
        bad = dataclasses.replace(
            estate,
            dependencies=estate.dependencies
            + (Dependency("esb", "esb", Link.SYNC, 1.0, 1.0, 1.0),),
        )
        with pytest.raises(ValueError, match="self-dependency"):
            assert_estate_is_consistent(bad)

    def test_duplicate_dependency(self, estate):
        bad = dataclasses.replace(
            estate, dependencies=estate.dependencies + (estate.dependencies[0],)
        )
        with pytest.raises(ValueError, match="duplicate dependency"):
            assert_estate_is_consistent(bad)

    def test_unknown_team(self, estate):
        with pytest.raises(ValueError, match="no capacity"):
            assert_estate_is_consistent(mutate(estate, "esb", team="nobody"))

    def test_non_positive_effort(self, estate):
        with pytest.raises(ValueError, match="non-positive effort"):
            assert_estate_is_consistent(mutate(estate, "esb", effort=0.0))

    def test_cloud_dearer_than_onprem(self, estate):
        with pytest.raises(ValueError, match="more in cloud"):
            assert_estate_is_consistent(mutate(estate, "esb", cloud_monthly=1e9))

    @pytest.mark.parametrize("bad_value", [0, 6, -1, 99])
    def test_criticality_range(self, estate, bad_value):
        with pytest.raises(ValueError, match="criticality out of range"):
            assert_estate_is_consistent(mutate(estate, "esb", criticality=bad_value))

    def test_unsplittable_carrying_split_costs(self, estate):
        src, tgt = next(
            (d.source, d.target) for d in estate.dependencies if not d.splittable
        )
        bad = mutate_dep(estate, src, tgt, hybrid_monthly=500.0)
        with pytest.raises(ValueError, match="carries split costs"):
            assert_estate_is_consistent(bad)

    def test_splittable_carrying_decoupling_cost(self, estate):
        src, tgt = next(
            (d.source, d.target) for d in estate.dependencies if d.splittable
        )
        bad = mutate_dep(estate, src, tgt, decouple_cost=1.0, decouple_effort=1.0)
        with pytest.raises(ValueError, match="carries a decoupling cost"):
            assert_estate_is_consistent(bad)

    def test_unsplittable_without_decoupling_cost(self, estate):
        src, tgt = next(
            (d.source, d.target) for d in estate.dependencies if not d.splittable
        )
        bad = mutate_dep(estate, src, tgt, decouple_cost=0.0)
        with pytest.raises(ValueError, match="cannot price the remediation"):
            assert_estate_is_consistent(bad)

    def test_component_larger_than_its_team_wave(self, estate):
        with pytest.raises(ValueError, match="can only supply"):
            assert_estate_is_consistent(mutate(estate, "esb", effort=10_000.0))

    def test_clean_estate_passes(self, estate):
        assert_estate_is_consistent(estate)


class TestDeterminism:
    def test_two_builds_are_equal(self):
        assert build_estate() == build_estate()

    def test_component_order_is_stable(self):
        a = [c.id for c in build_estate().components]
        b = [c.id for c in build_estate().components]
        assert a == b

    def test_frozen(self, estate):
        with pytest.raises(dataclasses.FrozenInstanceError):
            estate.components[0].effort = 1.0  # type: ignore[misc]
