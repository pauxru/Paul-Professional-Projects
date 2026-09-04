"""A lower bound on programme cost, and an exact optimum for small instances.

A heuristic that reports "the best plan I found costs GBP 2.96m" is telling
you about the heuristic, not about the estate. The question a sponsor
actually has is whether a better plan exists, and answering it needs a bound.

The bound here is built term by term from the cost model, and every term is
argued rather than asserted:

  * **Wave overhead.** No arrangement can use fewer waves than the busiest
    team's work divided by what that team can do in one wave.
  * **Run cost.** Cutting over earlier is cheaper because cloud costs less
    than on-premises. How early is limited by capacity, and the limit takes
    the form of a prefix bound on cutover months. Pairing the most expensive
    components with the earliest achievable cutovers, by the rearrangement
    inequality, gives the minimum.
  * **Hybrid build.** A one-off charged per separated dependency. For each
    unit, capacity limits which neighbours can share its wave; the rest are
    separated whatever the plan. This is a knapsack per unit, solved exactly.
  * **Remediation.** Already exact, from wave/units.py.

The gap between this bound and the best plan found is reported, and so is the
thing that makes the gap interpretable: on instances small enough to solve
exactly, how much of the gap is the solver falling short and how much is the
bound being loose. Reporting a gap without that split invites the reader to
attribute all of it to the solver, which is usually wrong.
"""

from __future__ import annotations

import itertools
import math
from dataclasses import dataclass

from .costs import (
    HORIZON_MONTHS,
    MAX_WAVE_MONTHS,
    WAVE_OVERHEAD,
    Plan,
    evaluate,
    is_feasible,
)
from .estate import Estate
from .units import Contraction


@dataclass(frozen=True)
class Bound:
    overhead: float
    run: float
    dual: float
    hybrid_build: float
    remediation: float
    min_waves: int

    @property
    def total(self) -> float:
        return self.overhead + self.run + self.dual + self.hybrid_build + self.remediation


def minimum_waves(estate: Estate, c: Contraction) -> int:
    """Fewest waves any feasible plan can use.

    Per team, total effort divided by what the team can supply in one wave,
    rounded up; the binding team wins. This ignores the packing problem
    entirely, so it is a bound rather than an answer -- indivisible units mean
    a team may be unable to reach its own arithmetic minimum.
    """
    per_team: dict[str, float] = {}
    for u in c.units:
        for team, eff in u.effort_by_team.items():
            per_team[team] = per_team.get(team, 0.0) + eff
    return max(
        int(math.ceil(eff / (estate.rate[team] * MAX_WAVE_MONTHS) - 1e-9))
        for team, eff in per_team.items()
    )


def _prefix_cutover_bounds(estate: Estate, c: Contraction, team: str) -> list[tuple[str, float]]:
    """Earliest achievable cutover month for the k-th unit of a team.

    If k units have cut over by month t, then all of their work for this team
    is done by t, so t is at least their combined effort over the team's rate.
    The smallest that combined effort can be is the sum of the k smallest unit
    efforts, giving a prefix bound that holds for any arrangement.
    """
    efforts = sorted(
        ((u.id, u.effort_by_team.get(team, 0.0)) for u in c.units if team in u.effort_by_team),
        key=lambda x: (x[1], x[0]),
    )
    out: list[tuple[str, float]] = []
    run = 0.0
    for uid, eff in efforts:
        run += eff
        out.append((uid, run / estate.rate[team]))
    return out


def _unit_solo_duration(estate: Estate, c: Contraction, uid: str) -> float:
    u = c.by_id(uid)
    return max(eff / estate.rate[t] for t, eff in u.effort_by_team.items())


def _run_bound(estate: Estate, c: Contraction, horizon: float) -> tuple[float, float]:
    """Lower bound on run cost and on dual-running cost.

    Run cost for a component is ``cloud * H + (onprem - cloud) * t``, and the
    estate invariant guarantees ``onprem > cloud``, so it increases with the
    cutover month. Licence waste is dropped -- it is non-negative and cannot be
    bounded below by anything but zero without fixing the arrangement, which
    makes the bound weaker and honest rather than stronger and wrong.
    """
    fixed = sum(comp.cloud_monthly * horizon for comp in estate.components)

    timed = 0.0
    teams = sorted(estate.rate)
    for team in teams:
        weights = {u.id: 0.0 for u in c.units if team in u.effort_by_team}
        for comp in estate.components:
            if comp.team != team:
                continue
            uid = c.unit_of(comp.id)
            weights[uid] = weights.get(uid, 0.0) + (comp.onprem_monthly - comp.cloud_monthly)
        prefixes = [p for _, p in _prefix_cutover_bounds(estate, c, team)]
        # Rearrangement inequality: to minimise sum(w_i * t_i) subject to the
        # sorted t values dominating the prefix bounds, pair the largest weight
        # with the earliest achievable month.
        ws = sorted(weights.values(), reverse=True)
        timed += sum(w * p for w, p in zip(ws, prefixes))

    dual = 0.0
    for comp in estate.components:
        solo = _unit_solo_duration(estate, c, c.unit_of(comp.id))
        dual += comp.cloud_monthly * solo

    return fixed + timed, dual


def _neighbour_build_costs(c: Contraction) -> dict[str, dict[str, float]]:
    out: dict[str, dict[str, float]] = {u.id: {} for u in c.units}
    for d in c.external:
        a, b = c.unit_of(d.source), c.unit_of(d.target)
        if a == b:
            continue
        out[a][b] = out[a].get(b, 0.0) + d.hybrid_build
        out[b][a] = out[b].get(a, 0.0) + d.hybrid_build
    return out


def _hybrid_build_bound(estate: Estate, c: Contraction, *, subset_limit: int = 16) -> float:
    """Build cost that no arrangement can avoid.

    For each unit, the neighbours that could share its wave are limited by
    per-team capacity. Choosing which neighbours to keep is a
    multi-dimensional knapsack -- one dimension per team -- and it is solved
    exactly by subset enumeration because the neighbourhoods are small.

    Each avoided edge is counted from both ends, so the sum is halved. That
    halving is what keeps the bound valid rather than merely plausible: an
    edge that both endpoints agree must be split is charged once.
    """
    nb = _neighbour_build_costs(c)
    caps = {t: estate.rate[t] * MAX_WAVE_MONTHS for t in estate.rate}
    total = 0.0
    for uid, costs in nb.items():
        if not costs:
            continue
        neigh = sorted(costs)
        if len(neigh) > subset_limit:
            raise ValueError(
                f"unit {uid!r} has {len(neigh)} neighbours, beyond the exact "
                f"subset limit of {subset_limit}; this bound's contract is "
                "validity, so it refuses rather than approximating silently"
            )
        base = c.by_id(uid).effort_by_team
        teams = sorted({t for v in neigh for t in c.by_id(v).effort_by_team} | set(base))
        cap = [caps[t] for t in teams]
        base_v = [base.get(t, 0.0) for t in teams]
        load = [[c.by_id(v).effort_by_team.get(t, 0.0) for t in teams] for v in neigh]
        val = [costs[v] for v in neigh]

        # Every subset, not just the largest feasible one. Feasibility is
        # monotone but value is not proportional to cardinality, so the
        # highest-value feasible subset can be smaller than the largest
        # feasible subset. Stopping at maximum cardinality understates
        # ``best_kept``, overstates the charge, and would let the "lower"
        # bound rise above a real plan's cost.
        best_kept = 0.0
        for mask in range(1 << len(neigh)):
            per = list(base_v)
            v = 0.0
            for j in range(len(neigh)):
                if mask >> j & 1:
                    v += val[j]
                    row = load[j]
                    for k in range(len(teams)):
                        per[k] += row[k]
            if v > best_kept and all(
                per[k] <= cap[k] + 1e-9 for k in range(len(teams))
            ):
                best_kept = v
        total += sum(costs.values()) - best_kept
    return total / 2.0


def lower_bound(
    estate: Estate,
    c: Contraction,
    *,
    remediation_cost: float,
    horizon: float = HORIZON_MONTHS,
) -> Bound:
    nw = minimum_waves(estate, c)
    run, dual = _run_bound(estate, c, horizon)
    return Bound(
        overhead=WAVE_OVERHEAD * nw,
        run=run,
        dual=dual,
        hybrid_build=_hybrid_build_bound(estate, c),
        remediation=remediation_cost,
        min_waves=nw,
    )


# ---------------------------------------------------------------------------
# Exact optimum on a reduced instance
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class SubInstance:
    """A slice of the estate small enough to solve by exhaustion."""

    units: tuple[str, ...]
    max_waves: int

    @property
    def search_space(self) -> int:
        return self.max_waves ** len(self.units)


def exact_optimum(
    estate: Estate,
    c: Contraction,
    sub: SubInstance,
    *,
    remediation_cost: float,
    space_limit: int = 4_000_000,
) -> tuple[Plan, float, int]:
    """Brute-force the best arrangement of a subset of units.

    Every assignment of the chosen units to wave slots is enumerated. Plans
    that leave a gap -- wave 2 used while wave 1 is empty -- are skipped as
    duplicates of a shorter plan, which cuts the space substantially without
    losing any distinct arrangement.

    The estate has 25 units; enumerating those over 6 waves is 6^25, so the
    full problem is not reachable this way and the report does not claim it is.
    What this buys is a *calibration*: on instances where the optimum is known,
    how far off is the heuristic and how loose is the bound.
    """
    if sub.search_space > space_limit:
        raise ValueError(
            f"search space {sub.search_space:,} exceeds the limit "
            f"{space_limit:,}; reduce the sub-instance rather than waiting"
        )

    sub_units = tuple(sub.units)
    keep = set(sub_units)
    sub_estate, reduced = restrict(estate, c, keep)

    best: tuple[float, Plan] | None = None
    evaluated = 0
    for combo in itertools.product(range(sub.max_waves), repeat=len(sub_units)):
        used = set(combo)
        if used != set(range(max(used) + 1)):
            continue
        waves: list[list[str]] = [[] for _ in range(max(used) + 1)]
        for uid, w in zip(sub_units, combo):
            waves[w].append(uid)
        plan = Plan(tuple(tuple(sorted(w)) for w in waves)).normalised()
        if not is_feasible(sub_estate, reduced, plan):
            continue
        evaluated += 1
        b, _ = evaluate(
            sub_estate, reduced, plan, remediation_cost=remediation_cost,
            horizon=HORIZON_MONTHS,
        )
        if best is None or b.total < best[0]:
            best = (b.total, plan)

    if best is None:
        raise ValueError("no feasible arrangement of the sub-instance")
    return best[1], best[0], evaluated


def restrict(
    estate: Estate, c: Contraction, keep: set[str]
) -> tuple[Estate, Contraction]:
    """A genuinely smaller instance of the same problem.

    Dependencies with an endpoint outside the slice are dropped rather than
    pinned, because pinning would require a cutover month for a unit that is
    not being scheduled. The estate is restricted alongside the contraction so
    the cost model sees a consistent world -- an estate carrying components
    that no unit owns would charge run cost for workloads the plan never
    touches, and the resulting "optimum" would be dominated by a constant that
    no arrangement can affect.
    """
    from dataclasses import replace as _replace

    from .units import Contraction as C

    units = tuple(u for u in c.units if u.id in keep)
    owner = {cid: uid for cid, uid in c.owner.items() if uid in keep}
    external = tuple(
        d
        for d in c.external
        if c.owner[d.source] in keep and c.owner[d.target] in keep
    )
    comps = tuple(comp for comp in estate.components if comp.id in owner)
    deps = tuple(
        d for d in estate.dependencies if d.source in owner and d.target in owner
    )
    return _replace(estate, components=comps, dependencies=deps), C(
        units, owner, external, c.broken
    )


def connected_slice(c: Contraction, n: int, *, seed_unit: str | None = None) -> SubInstance:
    """A connected subset of ``n`` units, grown breadth-first.

    Connected rather than random: a random subset would mostly have no
    dependencies between its members, which removes the term the optimiser
    exists to manage and would make the calibration far too flattering.
    """
    adj: dict[str, set[str]] = {u.id: set() for u in c.units}
    for d in c.external:
        a, b = c.unit_of(d.source), c.unit_of(d.target)
        if a != b:
            adj[a].add(b)
            adj[b].add(a)

    start = seed_unit or max(sorted(adj), key=lambda x: (len(adj[x]), x))
    seen = [start]
    frontier = sorted(adj[start])
    while len(seen) < n and frontier:
        nxt = frontier.pop(0)
        if nxt in seen:
            continue
        seen.append(nxt)
        for m in sorted(adj[nxt]):
            if m not in seen and m not in frontier:
                frontier.append(m)
    return SubInstance(tuple(sorted(seen)), max_waves=min(len(seen), 5))
