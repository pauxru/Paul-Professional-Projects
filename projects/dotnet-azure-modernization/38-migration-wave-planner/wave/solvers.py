"""Solvers, from the plan a steering committee would draw to the best one found.

Five strategies, deliberately including the two that get used in practice:

  * ``dependents_first`` -- topological layers, dependencies first. The plan on
    every migration slide deck.
  * ``least_critical_first`` -- least critical first, "de-risk by starting small".
    The plan every risk workshop produces.
  * ``coupling_clusters`` -- group by coupling strength, then order by
    dependency. What an architect draws.
  * ``local_search`` -- move and swap until no single change improves.
  * ``annealed`` -- simulated annealing from several starts, fixed seed.

The point of keeping the naive ones is not to beat them. It is that the report
has to be able to say what following the usual advice costs, in pounds, on a
specific estate. A search result with nothing to compare against is a number
nobody can act on.

Every solver returns a feasible plan or raises. Infeasibility here is a bug,
because wave/units.py has already established that a feasible plan exists.
"""

from __future__ import annotations

import math
import random
from dataclasses import dataclass
from typing import Callable, Iterable

from .costs import MAX_WAVE_MONTHS, Plan, evaluate, is_feasible
from .estate import Estate
from .units import Contraction

Objective = Callable[[Plan], float]


def make_objective(
    estate: Estate,
    c: Contraction,
    *,
    remediation_cost: float,
    count_licence: bool = True,
) -> Objective:
    """The cost a solver minimises.

    ``count_licence=False`` hides licence waste from the search while leaving
    it in the reported cost -- the controlled experiment for whether the
    optimiser genuinely exploits renewal dates or just happens to land well.
    """

    def f(plan: Plan) -> float:
        b, _ = evaluate(
            estate,
            c,
            plan,
            remediation_cost=remediation_cost,
            count_licence=count_licence,
        )
        return b.total

    return f


# ---------------------------------------------------------------------------
# Structure
# ---------------------------------------------------------------------------


def _unit_graph(c: Contraction) -> dict[str, set[str]]:
    g: dict[str, set[str]] = {u.id: set() for u in c.units}
    for d in c.external:
        a, b = c.unit_of(d.source), c.unit_of(d.target)
        if a != b:
            g[a].add(b)
            g[b].add(a)
    return g


def _directed_unit_edges(c: Contraction) -> list[tuple[str, str]]:
    out: list[tuple[str, str]] = []
    for d in c.external:
        a, b = c.unit_of(d.source), c.unit_of(d.target)
        if a != b:
            out.append((a, b))
    return out


def dependency_depth(c: Contraction) -> dict[str, int]:
    """Longest path to a leaf, over the unit graph with cycles broken.

    Contraction removes the shared-database cycles but not all of them: an
    estate with a hub in it has genuine cycles, and this one does. Cycles are
    broken by ignoring back edges in a deterministic DFS order rather than by
    failing, because refusing to plan an estate with a cyclic dependency graph
    would mean refusing to plan almost every real estate.
    """
    succ: dict[str, set[str]] = {u.id: set() for u in c.units}
    for a, b in _directed_unit_edges(c):
        succ[a].add(b)

    state: dict[str, int] = {u.id: 0 for u in c.units}
    depth: dict[str, int] = {}

    def visit(n: str) -> int:
        if state[n] == 1:
            return 0  # back edge; treat as a leaf for depth purposes
        if state[n] == 2:
            return depth[n]
        state[n] = 1
        d = 0
        for m in sorted(succ[n]):
            d = max(d, 1 + visit(m))
        state[n] = 2
        depth[n] = d
        return d

    for u in sorted(state):
        visit(u)
    return depth


def coupling_weight(c: Contraction) -> dict[tuple[str, str], float]:
    """Money at stake per month if a pair of units is separated."""
    from .costs import EGRESS_PER_GB

    w: dict[tuple[str, str], float] = {}
    for d in c.external:
        a, b = sorted((c.unit_of(d.source), c.unit_of(d.target)))
        if a == b:
            continue
        w[(a, b)] = w.get((a, b), 0.0) + d.hybrid_monthly + d.traffic_gb_mo * EGRESS_PER_GB
    return w


# ---------------------------------------------------------------------------
# Packing
# ---------------------------------------------------------------------------


def _fits(estate: Estate, c: Contraction, wave: list[str], uid: str) -> bool:
    per_team: dict[str, float] = {}
    for x in wave + [uid]:
        for team, eff in c.by_id(x).effort_by_team.items():
            per_team[team] = per_team.get(team, 0.0) + eff
    return all(
        eff <= estate.rate[team] * MAX_WAVE_MONTHS + 1e-9
        for team, eff in per_team.items()
    )


def pack_in_order(estate: Estate, c: Contraction, order: Iterable[str]) -> Plan:
    """First-fit-decreasing over a given sequence, respecting capacity.

    First *fit* rather than best fit: the sequence carries the strategy's
    intent, and a packer that reorders freely would quietly replace that
    intent with its own.
    """
    waves: list[list[str]] = [[]]
    for uid in order:
        placed = False
        for w in waves:
            if _fits(estate, c, w, uid):
                w.append(uid)
                placed = True
                break
        if not placed:
            waves.append([uid])
    return Plan(tuple(tuple(w) for w in waves)).normalised()


# ---------------------------------------------------------------------------
# Strategies
# ---------------------------------------------------------------------------


def dependents_first(estate: Estate, c: Contraction) -> Plan:
    """Deepest first: move the top of the dependency stack before its floor.

    The strangler instinct. Applications go early and the databases they read
    stay behind, which is attractive because the applications are what the
    business sees -- and expensive, because every one of those reads becomes
    a hybrid link for the rest of the programme.
    """
    depth = dependency_depth(c)
    order = sorted(c.units, key=lambda u: (-depth[u.id], -u.effort, u.id))
    return pack_in_order(estate, c, [u.id for u in order])


def least_critical_first(estate: Estate, c: Contraction) -> Plan:
    """Least critical first. The plan that comes out of every risk workshop.

    The reasoning is sound in isolation -- prove the landing zone on something
    that does not matter -- and it is exactly the reasoning the cost model is
    built to price, because the low-criticality components in this estate are
    the reporting stack, which is also the most chatty.
    """
    order = sorted(c.units, key=lambda u: (u.criticality, -u.effort, u.id))
    return pack_in_order(estate, c, [u.id for u in order])


def coupling_clusters(estate: Estate, c: Contraction) -> Plan:
    """Greedy agglomeration on coupling weight, then dependency order.

    Repeatedly merge the heaviest-coupled pair of groups whose combined
    effort still fits a wave. Equivalent to single-linkage clustering with a
    capacity stopping rule.
    """
    w = coupling_weight(c)
    groups: list[list[str]] = [[u.id] for u in sorted(c.units, key=lambda x: x.id)]

    def group_weight(a: list[str], b: list[str]) -> float:
        return sum(w.get(tuple(sorted((x, y))), 0.0) for x in a for y in b)  # type: ignore[arg-type]

    while True:
        best: tuple[float, int, int] | None = None
        for i in range(len(groups)):
            for j in range(i + 1, len(groups)):
                if not _group_fits(estate, c, groups[i] + groups[j]):
                    continue
                gw = group_weight(groups[i], groups[j])
                if gw <= 0:
                    continue
                if best is None or gw > best[0]:
                    best = (gw, i, j)
        if best is None:
            break
        _, i, j = best
        groups[i] = groups[i] + groups[j]
        del groups[j]

    depth = dependency_depth(c)
    groups.sort(key=lambda g: (-max(depth[x] for x in g), -len(g), min(g)))
    return Plan(tuple(tuple(sorted(g)) for g in groups)).normalised()


def _group_fits(estate: Estate, c: Contraction, group: list[str]) -> bool:
    per_team: dict[str, float] = {}
    for x in group:
        for team, eff in c.by_id(x).effort_by_team.items():
            per_team[team] = per_team.get(team, 0.0) + eff
    return all(
        eff <= estate.rate[team] * MAX_WAVE_MONTHS + 1e-9
        for team, eff in per_team.items()
    )


# ---------------------------------------------------------------------------
# Search
# ---------------------------------------------------------------------------


def _neighbours(plan: Plan, n_waves_cap: int) -> list[Plan]:
    """Move a unit to a different wave, or swap two units between waves.

    A move to an empty trailing wave is included so the search can change the
    *number* of waves, which matters because wave count is an output of the
    model and a search that could not vary it would be optimising inside a
    decision somebody else made.
    """
    waves = [list(w) for w in plan.waves]
    out: list[Plan] = []
    targets = list(range(len(waves))) + (
        [len(waves)] if len(waves) < n_waves_cap else []
    )
    for i, w in enumerate(waves):
        for uid in w:
            for j in targets:
                if i == j:
                    continue
                nw = [list(x) for x in waves]
                if j == len(nw):
                    nw.append([])
                nw[j].append(uid)
                nw[i].remove(uid)
                out.append(Plan(tuple(tuple(x) for x in nw)).normalised())
    for i in range(len(waves)):
        for j in range(i + 1, len(waves)):
            for a in waves[i]:
                for b in waves[j]:
                    nw = [list(x) for x in waves]
                    nw[i].remove(a)
                    nw[j].remove(b)
                    nw[i].append(b)
                    nw[j].append(a)
                    out.append(Plan(tuple(tuple(x) for x in nw)).normalised())
    return out


@dataclass
class SearchStats:
    evaluations: int = 0
    improvements: int = 0
    restarts: int = 0
    accepted_worse: int = 0


def local_search(
    estate: Estate,
    c: Contraction,
    start: Plan,
    obj: Objective,
    *,
    n_waves_cap: int = 12,
    stats: SearchStats | None = None,
) -> Plan:
    stats = stats if stats is not None else SearchStats()
    best = start.normalised()
    best_v = obj(best)
    stats.evaluations += 1
    while True:
        improved = False
        for cand in _neighbours(best, n_waves_cap):
            if not is_feasible(estate, c, cand):
                continue
            v = obj(cand)
            stats.evaluations += 1
            if v < best_v - 1e-6:
                best, best_v = cand, v
                improved = True
                stats.improvements += 1
                break
        if not improved:
            return best


def annealed(
    estate: Estate,
    c: Contraction,
    obj: Objective,
    starts: list[Plan],
    *,
    seed: int = 20240917,
    iterations: int = 9000,
    n_waves_cap: int = 12,
    stats: SearchStats | None = None,
) -> Plan:
    """Simulated annealing, seeded, then polished by local search.

    The seed is fixed and the RNG is a private ``random.Random`` rather than
    the module-level one, so the result does not depend on what else has drawn
    a random number in this process. That is what lets docs/results.md be
    compared byte for byte between runs.
    """
    stats = stats if stats is not None else SearchStats()
    rng = random.Random(seed)
    overall = min(starts, key=obj)
    overall_v = obj(overall)

    for s in starts:
        stats.restarts += 1
        cur = s.normalised()
        cur_v = obj(cur)
        stats.evaluations += 1
        t0, t1 = max(cur_v * 0.02, 1.0), max(cur_v * 1e-5, 1e-3)
        for k in range(iterations):
            temp = t0 * (t1 / t0) ** (k / max(iterations - 1, 1))
            cand = _random_move(cur, rng, n_waves_cap)
            if cand is None or not is_feasible(estate, c, cand):
                continue
            v = obj(cand)
            stats.evaluations += 1
            d = v - cur_v
            if d < 0 or rng.random() < math.exp(-d / temp):
                if d > 0:
                    stats.accepted_worse += 1
                cur, cur_v = cand, v
                if cur_v < overall_v - 1e-9:
                    overall, overall_v = cur, cur_v
                    stats.improvements += 1
        polished = local_search(estate, c, cur, obj, n_waves_cap=n_waves_cap, stats=stats)
        pv = obj(polished)
        if pv < overall_v - 1e-9:
            overall, overall_v = polished, pv

    return local_search(estate, c, overall, obj, n_waves_cap=n_waves_cap, stats=stats)


def _random_move(plan: Plan, rng: random.Random, n_waves_cap: int) -> Plan | None:
    waves = [list(w) for w in plan.waves]
    if not waves:
        return None
    if rng.random() < 0.65 or len(waves) < 2:
        i = rng.randrange(len(waves))
        if not waves[i]:
            return None
        uid = rng.choice(waves[i])
        hi = len(waves) + (1 if len(waves) < n_waves_cap else 0)
        j = rng.randrange(hi)
        if j == i:
            return None
        if j == len(waves):
            waves.append([])
        waves[i].remove(uid)
        waves[j].append(uid)
    else:
        i, j = rng.sample(range(len(waves)), 2)
        if not waves[i] or not waves[j]:
            return None
        a, b = rng.choice(waves[i]), rng.choice(waves[j])
        waves[i].remove(a)
        waves[j].remove(b)
        waves[i].append(b)
        waves[j].append(a)
    return Plan(tuple(tuple(w) for w in waves)).normalised()


SOLVERS: dict[str, Callable[[Estate, Contraction], Plan]] = {
    "dependents_first": dependents_first,
    "least_critical_first": least_critical_first,
    "coupling_clusters": coupling_clusters,
}
