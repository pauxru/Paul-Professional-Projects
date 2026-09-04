"""The cost model.

Four things drive the number, and only one of them is in most business cases.

  * **Dual running.** From the moment a unit's migration starts until it cuts
    over, the workload exists twice and is paid for twice.
  * **Licence waste.** On-premises licences are bought annually and are not
    refunded on decommission. Cut over in month 2 of a term and you have
    thrown away ten months. This makes the *right* wave for a component
    partly a function of its renewal date, which no capacity-driven plan
    considers.
  * **Hybrid links and egress.** Every dependency whose two ends land in
    different waves needs a temporary connection: a one-off build, a monthly
    run cost, and per-gigabyte egress for as long as the two ends are apart.
    This is the term that punishes many small waves.
  * **Programme overhead.** Change board, cutover weekend, comms, hypercare.
    Fixed per wave, which is the other half of the same tension.

The tension is the point. Dual running and overhead push towards few large
waves; capacity, risk and blast radius push towards many small ones; hybrid
cost punishes *separating* things regardless of wave count. The number of
waves is therefore an output of the model, not an input to it.
"""

from __future__ import annotations

import math
from dataclasses import dataclass

from .estate import Estate
from .units import Contraction

#: Azure egress, GBP per GB, standard tier. Inbound is free, which is why
#: seeding 6.1 TB of claims documents costs nothing and reading them back
#: across a hybrid boundary every month costs a great deal.
EGRESS_PER_GB = 0.070

#: What one wave costs to run irrespective of what is in it: change advisory
#: board, a cutover weekend at weekend rates, comms, and two weeks of
#: hypercare. Deliberately large, because it is deliberately large in reality
#: and because underestimating it is how programmes end up with 14 waves.
WAVE_OVERHEAD = 85_000.0

#: Governance limit. A wave longer than this is not a wave, it is a
#: programme with a wave-shaped name, and it loses the property that makes
#: waves useful -- a decision point at the end of each one.
MAX_WAVE_MONTHS = 2.5

#: Months in which a cutover is forbidden. 10 and 11 are the November and
#: December trading peak; 2 is the March financial year end. A cutover that
#: would land in one of these slips to the far side, and the slip is paid for
#: in dual running.
FREEZE_MONTHS = frozenset({2, 10, 11})

#: How long the business case is measured over, in months from programme
#: start. Long enough to include a tail of steady-state saving, because a
#: horizon that ends at cutover makes every migration look like a pure cost.
HORIZON_MONTHS = 48.0

#: Discount rate per annum used for present value. Migrations spend early and
#: save late, so the choice of rate is not cosmetic.
DISCOUNT_ANNUAL = 0.08


@dataclass(frozen=True)
class Plan:
    """An ordered sequence of waves, each a set of unit ids."""

    waves: tuple[tuple[str, ...], ...]

    @property
    def n_waves(self) -> int:
        return len(self.waves)

    @property
    def units(self) -> tuple[str, ...]:
        return tuple(u for w in self.waves for u in w)

    def wave_of(self) -> dict[str, int]:
        return {u: i for i, w in enumerate(self.waves) for u in w}

    def normalised(self) -> "Plan":
        """Drop empty waves and sort within each wave.

        Two plans that differ only in the order of units *within* a wave are
        the same plan; normalising makes equality and hashing agree with that,
        which matters because the local search dedupes candidates.
        """
        return Plan(tuple(tuple(sorted(w)) for w in self.waves if w))


@dataclass(frozen=True)
class Schedule:
    """A plan placed on the calendar."""

    plan: Plan
    #: Nominal duration of each wave in months, before freeze slippage.
    durations: tuple[float, ...]
    #: Month at which each wave's cutover completes, after freeze slippage.
    cutovers: tuple[float, ...]
    #: Months added purely because a cutover would have landed in a freeze.
    freeze_slip: tuple[float, ...]

    @property
    def end_month(self) -> float:
        return self.cutovers[-1] if self.cutovers else 0.0

    def cutover_of_unit(self) -> dict[str, float]:
        w = self.plan.wave_of()
        return {u: self.cutovers[i] for u, i in w.items()}


def wave_duration(estate: Estate, c: Contraction, wave: tuple[str, ...]) -> float:
    """Months to deliver a wave.

    Teams work in parallel inside a wave, so the wave takes as long as its
    busiest team. This is also where the merge bias in wave/risk.py comes
    from: a maximum of random variables has a mean above the maximum of their
    means, so a plan costed on mean team effort is optimistic before any
    estimate is wrong.
    """
    per_team: dict[str, float] = {}
    for uid in wave:
        for team, eff in c.by_id(uid).effort_by_team.items():
            per_team[team] = per_team.get(team, 0.0) + eff
    if not per_team:
        return 0.0
    return max(eff / estate.rate[team] for team, eff in per_team.items())


def is_feasible(estate: Estate, c: Contraction, plan: Plan) -> bool:
    """No team may be asked for more than it can supply inside one wave."""
    for wave in plan.waves:
        per_team: dict[str, float] = {}
        for uid in wave:
            for team, eff in c.by_id(uid).effort_by_team.items():
                per_team[team] = per_team.get(team, 0.0) + eff
        for team, eff in per_team.items():
            if eff > estate.rate[team] * MAX_WAVE_MONTHS + 1e-9:
                return False
    return True


def _out_of_freeze(month: float) -> float:
    """Push a cutover forward until it is out of a freeze window.

    Freeze months repeat annually, so a cutover landing in a run of frozen
    months slips to the end of that run. The loop is bounded by the length of
    a year plus one; a freeze covering every month would be a modelling error
    and is rejected at import.
    """
    guard = 0
    while int(math.floor(month)) % 12 in FREEZE_MONTHS:
        month = float(math.floor(month) + 1)
        guard += 1
        if guard > 12:  # pragma: no cover - excluded by the import-time check
            raise ValueError("freeze windows cover the whole year")
    return month


if len(FREEZE_MONTHS) >= 12:  # pragma: no cover
    raise ValueError("FREEZE_MONTHS covers every month; no cutover is possible")


def schedule(estate: Estate, c: Contraction, plan: Plan, *, start_month: float = 0.0,
             freeze: bool = True) -> Schedule:
    durations: list[float] = []
    cutovers: list[float] = []
    slips: list[float] = []
    t = start_month
    for wave in plan.waves:
        d = wave_duration(estate, c, wave)
        t += d
        raw = t
        t = _out_of_freeze(t) if freeze else t
        slips.append(t - raw)
        durations.append(d)
        cutovers.append(t)
    return Schedule(plan, tuple(durations), tuple(cutovers), tuple(slips))


@dataclass(frozen=True)
class CostBreakdown:
    onprem_run: float
    cloud_run: float
    dual_running: float
    licence_waste: float
    hybrid_build: float
    hybrid_run: float
    egress: float
    overhead: float
    remediation: float

    @property
    def total(self) -> float:
        return (
            self.onprem_run
            + self.cloud_run
            + self.dual_running
            + self.licence_waste
            + self.hybrid_build
            + self.hybrid_run
            + self.egress
            + self.overhead
            + self.remediation
        )

    def items(self) -> list[tuple[str, float]]:
        return [
            ("on-prem run", self.onprem_run),
            ("cloud run", self.cloud_run),
            ("dual running", self.dual_running),
            ("licence waste", self.licence_waste),
            ("hybrid build", self.hybrid_build),
            ("hybrid run", self.hybrid_run),
            ("egress", self.egress),
            ("wave overhead", self.overhead),
            ("remediation", self.remediation),
        ]


def cost_floor(
    estate: Estate, remediation_cost: float, horizon: float = HORIZON_MONTHS
) -> float:
    """Programme cost that no arrangement can avoid.

    Over the horizon each component is billed on-prem until its cutover and
    in the cloud afterwards, so the cheapest conceivable run cost for it is
    ``min(onprem, cloud) * horizon`` -- achieved only by an instantaneous
    move. Remediation is fixed by feasibility rather than by ordering, so it
    is floor too.

    Reported because comparing arrangements on the total flatters all of them
    equally. The spread between a good plan and a bad one lives entirely in
    the discretionary remainder, and quoting the total hides how large that
    spread is in proportional terms.
    """
    run = sum(min(c.onprem_monthly, c.cloud_monthly) for c in estate.components)
    return run * horizon + remediation_cost


def licence_waste(licence_annual: float, renews_month: int, cutover: float) -> float:
    """Unused remainder of the licence term in force at cutover.

    A term starts at ``renews_month`` and runs twelve months. Decommissioning
    part way through wastes the balance. Nothing is refunded and nothing is
    pro-rated, which is exactly why this term rewards cutting over shortly
    before a renewal and punishes cutting over shortly after one.
    """
    if licence_annual <= 0:
        return 0.0
    months_into_term = (cutover - renews_month) % 12.0
    remaining = 12.0 - months_into_term
    if remaining >= 12.0:
        remaining = 0.0
    return licence_annual * (remaining / 12.0)


def evaluate(
    estate: Estate,
    c: Contraction,
    plan: Plan,
    *,
    remediation_cost: float = 0.0,
    horizon: float = HORIZON_MONTHS,
    freeze: bool = True,
    count_licence: bool = True,
) -> tuple[CostBreakdown, Schedule]:
    """Price a plan.

    ``count_licence=False`` hides the licence-waste term. It exists so a
    solver can be run blind to renewal dates and then charged the real bill,
    which is the only honest way to tell whether the optimiser exploits those
    dates or merely lands on them.
    """
    sch = schedule(estate, c, plan, freeze=freeze)
    cut = sch.cutover_of_unit()
    wave_of = plan.wave_of()

    onprem = cloud = dual = waste = 0.0
    for comp in estate.components:
        uid = c.unit_of(comp.id)
        t = cut[uid]
        w = wave_of[uid]
        build_months = sch.durations[w]
        t = min(t, horizon)
        onprem += comp.onprem_monthly * t
        cloud += comp.cloud_monthly * max(horizon - t, 0.0)
        # While a unit is being built in the cloud its on-premises twin is
        # still serving traffic, so the cloud footprint is additional.
        dual += comp.cloud_monthly * min(build_months, t)
        if count_licence:
            waste += licence_waste(comp.licence_annual, comp.licence_renews, t)

    h_build = h_run = egress = 0.0
    for d in c.external:
        us, ut = c.unit_of(d.source), c.unit_of(d.target)
        if us == ut:
            continue
        gap = abs(cut[us] - cut[ut])
        if gap <= 1e-9:
            continue
        h_build += d.hybrid_build
        h_run += d.hybrid_monthly * gap
        egress += d.traffic_gb_mo * EGRESS_PER_GB * gap

    return (
        CostBreakdown(
            onprem_run=onprem,
            cloud_run=cloud,
            dual_running=dual,
            licence_waste=waste,
            hybrid_build=h_build,
            hybrid_run=h_run,
            egress=egress,
            overhead=WAVE_OVERHEAD * plan.n_waves,
            remediation=remediation_cost,
        ),
        sch,
    )


def do_nothing_cost(estate: Estate, horizon: float = HORIZON_MONTHS) -> float:
    """The counterfactual. Without it, every migration looks like a pure cost."""
    run = sum(c.onprem_monthly for c in estate.components) * horizon
    lic = sum(c.licence_annual for c in estate.components) * (horizon / 12.0)
    return run + lic


def present_value(monthly: float, month: float, annual_rate: float = DISCOUNT_ANNUAL) -> float:
    return monthly / ((1.0 + annual_rate) ** (month / 12.0))


def payback_month(
    estate: Estate,
    c: Contraction,
    plan: Plan,
    *,
    remediation_cost: float,
    limit: float = 240.0,
) -> float | None:
    """First month at which cumulative programme spend falls below do-nothing.

    Returns ``None`` if it never does inside ``limit`` months, which is a
    result worth reporting rather than an error -- some migrations genuinely
    do not pay back, and a planner that cannot say so is not a planner.
    """
    lo, hi = 0.0, limit
    def delta(h: float) -> float:
        b, _ = evaluate(estate, c, plan, remediation_cost=remediation_cost, horizon=h)
        return b.total - do_nothing_cost(estate, h)

    if delta(limit) > 0:
        return None
    for _ in range(60):
        mid = 0.5 * (lo + hi)
        if delta(mid) > 0:
            lo = mid
        else:
            hi = mid
    return hi
