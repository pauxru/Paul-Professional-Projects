"""Contraction of the estate into indivisible migration units, and the
feasibility question that follows.

The planner's first job is not to produce a schedule. It is to establish
whether a schedule exists.

Components joined by an unsplittable link -- two things writing the same
schema -- must move together, because the alternative is a distributed
transaction over a WAN. Contracting those links gives the real unit of
planning, and the real unit is usually much larger than anybody's slide
suggests. If any unit needs more effort from a team than that team can supply
in a single wave, no wave plan exists at any cost, and every schedule anyone
has drawn for that estate is fiction.

When that happens, the useful output is the cheapest set of couplings to break
in order to restore feasibility. That set is computed exactly here rather than
approximated, because it is small and because the number is going to be
argued about.
"""

from __future__ import annotations

from dataclasses import dataclass
from functools import cached_property

from .estate import Component, Dependency, Estate


@dataclass(frozen=True)
class Unit:
    """A set of components that must move in the same wave."""

    id: str
    members: tuple[str, ...]
    #: Effort broken down by owning team, because capacity is per team.
    effort_by_team: dict[str, float]
    effort: float
    criticality: int
    data_gb: float

    @property
    def is_cluster(self) -> bool:
        return len(self.members) > 1


@dataclass(frozen=True)
class Contraction:
    units: tuple[Unit, ...]
    #: unit id for each component id
    owner: dict[str, str]
    #: Dependencies that survive contraction, i.e. those whose ends landed in
    #: different units. Keyed by the unordered unit pair.
    external: tuple[Dependency, ...]
    #: Couplings that were broken to obtain this contraction (empty for the
    #: estate as declared).
    broken: tuple[tuple[str, str], ...] = ()

    def unit_of(self, cid: str) -> str:
        return self.owner[cid]

    @cached_property
    def _index(self) -> dict[str, Unit]:
        return {u.id: u for u in self.units}

    def by_id(self, uid: str) -> Unit:
        # Indexed rather than scanned: this is called once per unit per plan
        # evaluation and the annealer makes tens of thousands of those. It
        # also has to raise KeyError rather than StopIteration, which a
        # generator upstream would otherwise swallow into a silent empty
        # result.
        try:
            return self._index[uid]
        except KeyError:
            raise KeyError(f"no unit {uid!r} in this contraction") from None


def contract(
    estate: Estate, broken: frozenset[tuple[str, str]] = frozenset()
) -> Contraction:
    """Collapse unsplittable links into units.

    ``broken`` names unsplittable edges that remediation has already removed;
    those no longer force their endpoints together.
    """
    parent: dict[str, str] = {c.id: c.id for c in estate.components}

    def find(x: str) -> str:
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    def union(a: str, b: str) -> None:
        ra, rb = find(a), find(b)
        if ra != rb:
            # Union by id keeps the representative deterministic, which keeps
            # unit ids stable across runs and therefore keeps the report
            # byte-reproducible.
            lo, hi = sorted((ra, rb))
            parent[hi] = lo

    for d in estate.dependencies:
        if d.splittable or (d.source, d.target) in broken:
            continue
        union(d.source, d.target)

    groups: dict[str, list[Component]] = {}
    for c in estate.components:
        groups.setdefault(find(c.id), []).append(c)

    units: list[Unit] = []
    owner: dict[str, str] = {}
    for root in sorted(groups):
        members = sorted(c.id for c in groups[root])
        comps = [estate.by_id(m) for m in members]
        by_team: dict[str, float] = {}
        for c in comps:
            by_team[c.team] = by_team.get(c.team, 0.0) + c.effort
        u = Unit(
            id=root,
            members=tuple(members),
            effort_by_team=dict(sorted(by_team.items())),
            effort=sum(c.effort for c in comps),
            criticality=max(c.criticality for c in comps),
            data_gb=sum(c.data_gb for c in comps),
        )
        units.append(u)
        for m in members:
            owner[m] = root

    external = tuple(
        d
        for d in estate.dependencies
        if owner[d.source] != owner[d.target]
    )
    return Contraction(tuple(units), owner, external, tuple(sorted(broken)))


# ---------------------------------------------------------------------------
# Feasibility
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class Infeasibility:
    unit_id: str
    team: str
    required: float
    available: float

    @property
    def excess(self) -> float:
        return self.required - self.available


def infeasible_units(estate: Estate, c: Contraction) -> list[Infeasibility]:
    """Units that cannot fit in one wave no matter how the plan is arranged.

    A unit is indivisible, so all of its effort for a given team lands in one
    wave. If that exceeds the team's per-wave capacity there is no arrangement
    that helps -- not a longer schedule, not more waves, not a different order.
    """
    out: list[Infeasibility] = []
    for u in c.units:
        for team, eff in u.effort_by_team.items():
            cap = estate.capacity[team]
            if eff > cap + 1e-9:
                out.append(Infeasibility(u.id, team, eff, cap))
    return sorted(out, key=lambda i: (-i.excess, i.unit_id, i.team))


def _breakable(estate: Estate) -> list[Dependency]:
    return [d for d in estate.dependencies if not d.splittable]


def cheapest_feasible_decoupling(
    estate: Estate, *, exhaustive_limit: int = 20
) -> tuple[frozenset[tuple[str, str]], float, dict[str, int]]:
    """The least expensive set of couplings to break to make a plan exist.

    Exact, by enumerating subsets in increasing size. The estate declares 10
    unsplittable edges, so the search space is 2^10 and the exact answer is
    affordable; the ``exhaustive_limit`` guard exists so that a larger estate
    fails loudly rather than silently running for a week.

    Returns the edge set, its money cost, and a small dict of search
    statistics -- the statistics are reported because "we found the optimum"
    and "we found something after looking at 41 candidates" are different
    claims and the report should be able to make the stronger one.
    """
    edges = _breakable(estate)
    if len(edges) > exhaustive_limit:
        raise ValueError(
            f"{len(edges)} unsplittable edges is beyond the exact search limit "
            f"of {exhaustive_limit}; this function's contract is optimality, "
            "so it refuses rather than degrading to a heuristic silently"
        )

    keyed = {(d.source, d.target): d for d in edges}
    if not infeasible_units(estate, contract(estate)):
        return frozenset(), 0.0, {
            "evaluated": 0,
            "subsets": 0,
            "feasible": 0,
            "size": 0,
        }

    # Every subset, not the first feasible cardinality. Breaking more edges
    # can only help feasibility, so the smallest feasible set is easy to find
    # and tempting to return -- but cost is not monotone in cardinality. One
    # expensive break can be dearer than three cheap ones, so a search that
    # stops at the smallest feasible size returns a feasible answer while
    # claiming an optimal one. 2^10 subsets is cheap; the claim is not.
    evaluated = 0
    feasible = 0
    best: tuple[float, int, frozenset[tuple[str, str]]] | None = None
    keys = sorted(keyed)
    for mask in range(1, 1 << len(keys)):
        evaluated += 1
        chosen = frozenset(keys[j] for j in range(len(keys)) if mask >> j & 1)
        if infeasible_units(estate, contract(estate, chosen)):
            continue
        feasible += 1
        cost = sum(keyed[k].decouple_cost for k in chosen)
        # Ties broken by cardinality then by sorted edge list, so the result
        # does not depend on enumeration order.
        cand = (cost, len(chosen), chosen)
        if best is None or (cost, len(chosen), sorted(chosen)) < (
            best[0],
            best[1],
            sorted(best[2]),
        ):
            best = cand

    if best is None:
        raise ValueError(
            "no subset of the unsplittable edges makes the estate feasible; "
            "the capacity model or the estate is wrong"
        )
    return best[2], best[0], {
        "evaluated": evaluated,
        "subsets": 2 ** len(keys),
        "feasible": feasible,
        "size": best[1],
    }


def decoupling_effort(estate: Estate, broken: frozenset[tuple[str, str]]) -> float:
    keyed = {(d.source, d.target): d for d in estate.dependencies}
    return sum(keyed[k].decouple_effort for k in broken)


def largest_units(c: Contraction, n: int = 6) -> list[Unit]:
    return sorted(c.units, key=lambda u: (-u.effort, u.id))[:n]
