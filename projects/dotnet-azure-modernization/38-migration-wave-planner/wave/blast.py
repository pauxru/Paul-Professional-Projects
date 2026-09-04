"""Blast radius: what else stops working when a wave goes wrong at 2am.

Cost and schedule are the questions a programme board asks. This is the one it
asks at the incident review afterwards, and it is the one a plan is least
likely to have an answer to, because blast radius is a property of the
*ordering* rather than of any component.

Two exposures are distinguished, because they fail differently:

  * **Cutover exposure.** During a wave's cutover the components being moved
    are unavailable or degraded, and so is anything that depends on them
    through a synchronous path. An asynchronous dependency queues and
    catches up; a synchronous one returns errors to a customer.
  * **Standing hybrid exposure.** Between the two cutovers of a split
    dependency there is a temporary link carrying production traffic across a
    boundary it was never designed to cross. That link is live for months, not
    for a weekend, and it is the failure mode that actually causes the
    incidents. Peak concurrent live links is therefore reported alongside the
    per-wave figure.

Exposure is weighted by criticality rather than counted, because six back
office report consumers and one claims intake API are not the same event.
"""

from __future__ import annotations

from dataclasses import dataclass

from .costs import Plan, evaluate
from .estate import Estate, Link
from .units import Contraction

#: Link types that propagate an outage synchronously. A queue absorbs a
#: cutover window; an HTTP call does not. Replication and batch reads are
#: excluded because a nightly job that runs late is not an incident, and
#: counting it as one would drown the real signal.
PROPAGATING = frozenset({Link.SYNC, Link.SHARED_DB, Link.FILE})


@dataclass(frozen=True)
class WaveExposure:
    wave: int
    moving: tuple[str, ...]
    exposed: tuple[str, ...]
    weighted: float
    live_links: int

    @property
    def size(self) -> int:
        return len(self.exposed)


def _dependents(estate: Estate) -> dict[str, set[str]]:
    """Who breaks if this breaks, transitively, over propagating links only.

    Deliberately a plain search per node rather than a memoised recursion.
    The memoised version is the natural thing to write and it is wrong on any
    graph with a cycle: the set computed for a node reached *inside* a cycle
    is missing whichever node was already on the stack, and caching it under
    the node alone leaks that truncation to every later caller. The symptom is
    nasty -- the answer for a component depends on which component the outer
    loop happened to visit first, so the closure is silently order-dependent
    and only on estates that have a cycle in them. This estate has a message
    hub and a warehouse in it, so that is not a corner case.

    A node is never reported as its own dependent even when it sits on a
    cycle: :func:`wave_exposures` counts the moving component itself, and
    "this outage takes itself down" is not information a change board can use.
    """
    direct: dict[str, set[str]] = {c.id: set() for c in estate.components}
    for d in estate.dependencies:
        if d.link in PROPAGATING:
            direct[d.target].add(d.source)

    out: dict[str, set[str]] = {}
    for c in estate.components:
        seen: set[str] = set()
        stack = sorted(direct[c.id])
        while stack:
            n = stack.pop()
            if n in seen:
                continue
            seen.add(n)
            stack.extend(sorted(direct[n] - seen))
        seen.discard(c.id)
        out[c.id] = seen
    return out


def wave_exposures(estate: Estate, c: Contraction, plan: Plan) -> list[WaveExposure]:
    dep = _dependents(estate)
    _, sch = evaluate(estate, c, plan, remediation_cost=0.0)
    cut = sch.cutover_of_unit()

    out: list[WaveExposure] = []
    for i, wave in enumerate(plan.waves):
        moving = sorted(m for uid in wave for m in c.by_id(uid).members)
        exposed: set[str] = set(moving)
        for m in moving:
            exposed |= dep[m]
        weighted = sum(estate.by_id(x).criticality for x in exposed)

        # Links that are live in the window opened by this wave's cutover:
        # one end has moved, the other has not. Every unit in a wave cuts over
        # at the same instant, so the wave's cutover time is the only clock
        # that matters here.
        t = sch.cutovers[i]
        live = 0
        for d in c.external:
            us, ut = c.unit_of(d.source), c.unit_of(d.target)
            if us == ut:
                continue
            if (cut[us] <= t) != (cut[ut] <= t):
                live += 1

        out.append(
            WaveExposure(
                wave=i + 1,
                moving=tuple(moving),
                exposed=tuple(sorted(exposed)),
                weighted=float(weighted),
                live_links=live,
            )
        )
    return out


def peak_exposure(exposures: list[WaveExposure]) -> float:
    return max((e.weighted for e in exposures), default=0.0)


def peak_live_links(exposures: list[WaveExposure]) -> int:
    return max((e.live_links for e in exposures), default=0)


def exposure_of_plan(estate: Estate, c: Contraction, plan: Plan) -> tuple[float, int]:
    ex = wave_exposures(estate, c, plan)
    return peak_exposure(ex), peak_live_links(ex)


def pareto(points: list[tuple[float, float, str]]) -> list[tuple[float, float, str]]:
    """Non-dominated (cost, exposure) pairs, cheapest first.

    Both axes are minimised. Dominance is strict: ``q`` dominates ``p`` only
    if it is no worse on both axes *and* strictly better on at least one.
    Written that way deliberately -- the weaker test ``q <= p on both`` makes
    two plans with identical coordinates dominate each other, and both then
    disappear from the frontier. That is not a hypothetical: the plans this
    frontier compares are produced by different heuristics over the same
    estate, and heuristics agreeing is the normal case, not the odd one.
    Ties are kept once, ordered by name, so the output is deterministic.
    """
    best: dict[tuple[float, float], tuple[float, float, str]] = {}
    for p in points:
        dominated = any(
            q[0] <= p[0] and q[1] <= p[1] and (q[0] < p[0] or q[1] < p[1])
            for q in points
        )
        if dominated:
            continue
        key = (p[0], p[1])
        if key not in best or p[2] < best[key][2]:
            best[key] = p
    return sorted(best.values())
