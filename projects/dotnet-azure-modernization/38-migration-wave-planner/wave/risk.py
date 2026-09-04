"""Schedule risk: what the plan actually finishes, as opposed to what it says.

A wave plan carries a date. That date is computed from point estimates of
effort, and it is wrong in a direction that is knowable in advance.

Two mechanisms, both modelled here:

**Merge bias.** Teams work in parallel inside a wave, so the wave takes as long
as its slowest team. The expected value of a maximum exceeds the maximum of the
expected values. A plan costed on mean effort per team therefore reports a date
that is not the P50 -- it is optimistic before a single estimate is wrong, and
it gets more optimistic the more teams a wave involves. This is the
best-documented failure of critical-path planning and it is still in almost
every migration plan.

**Correlation.** Effort overruns are not independent. The same unknowns -- an
undocumented integration, a landing zone that needs rework, a security review
nobody scheduled -- hit several teams at once and persist across waves. The
model separates the two levels because they push in *opposite* directions:
correlation between teams inside a wave makes the maximum less extreme and so
*reduces* merge bias, while correlation across waves inflates the variance of
the sum and so *fattens the tail*. Which effect wins is an empirical question,
which is why it is measured rather than argued.

Everything is drawn from a seeded generator. Two runs of the report produce
identical percentiles, which is what allows docs/results.md to be compared byte
for byte.
"""

from __future__ import annotations

import math
from dataclasses import dataclass

import numpy as np

from .costs import Plan, _out_of_freeze
from .estate import Estate
from .units import Contraction

#: Log-normal shape parameter for effort. sigma = 0.35 puts the 90th
#: percentile at about 1.57x the median, which is optimistic by the standards
#: of the published overrun literature and is chosen so the findings cannot be
#: dismissed as an artefact of a pessimistic prior.
SIGMA = 0.35

#: Share of variance from a single programme-wide factor. Correlates every
#: team in every wave.
RHO_PROGRAMME = 0.25

#: Additional share from a per-wave factor. Correlates teams inside one wave
#: without correlating across waves.
RHO_WAVE = 0.20


@dataclass(frozen=True)
class RiskResult:
    deterministic: float
    samples: np.ndarray
    rho_programme: float
    rho_wave: float
    sigma: float

    def pct(self, q: float) -> float:
        return float(np.percentile(self.samples, q))

    @property
    def mean(self) -> float:
        return float(self.samples.mean())

    @property
    def merge_bias(self) -> float:
        """Mean outcome minus the plan's own date.

        Mean rather than median, because the statement being measured is
        ``E[max] > max[E]`` -- the mean of a maximum exceeds the maximum of
        the means. It is also the only one of the two that survives change
        freezes: freezes snap outcomes onto month boundaries, so the median
        can sit inside an atom of probability and report a slip of exactly
        zero while the distribution is plainly late. See ``median_slip``.
        """
        return self.mean - self.deterministic

    @property
    def median_slip(self) -> float:
        """Median outcome minus the plan's own date.

        Reported alongside :attr:`merge_bias` rather than instead of it. Under
        change freezes this is a quantised statistic and can be misleading;
        see :meth:`mass_at`.
        """
        return self.pct(50) - self.deterministic

    @property
    def p90_overrun(self) -> float:
        return self.pct(90) - self.deterministic

    def mass_at(self, month: float, *, tol: float = 1e-9) -> float:
        """Share of outcomes landing on exactly ``month``.

        Change freezes make the outcome distribution mixed rather than
        continuous: every draw whose unconstrained end lands anywhere inside a
        run of frozen months is pushed to the same date, so the trailing edge
        of a freeze carries an atom of probability whose width is the length
        of the freeze. Percentile statistics read across an atom are not
        wrong, but they are not smooth either, and a planner quoting "the
        median is the plan date" should know whether that is a statement about
        the schedule or about the freeze calendar.
        """
        return float(np.mean(np.abs(self.samples - month) <= tol))

    def largest_atom(self) -> tuple[float, float]:
        """The heaviest single outcome, as ``(month, share)``.

        Zero share on a continuous distribution; on a frozen one it locates
        the trailing edge of the longest freeze run the programme can hit.
        """
        vals, counts = np.unique(self.samples, return_counts=True)
        i = int(np.argmax(counts))
        share = float(counts[i]) / len(self.samples)
        return float(vals[i]), (share if counts[i] > 1 else 0.0)

    def median_in_atom(self, *, floor: float = 0.02) -> bool:
        """Whether P50 sits on an atom heavy enough to distort it."""
        return self.mass_at(self.pct(50)) >= floor


def wave_team_effort(c: Contraction, plan: Plan) -> list[dict[str, float]]:
    out: list[dict[str, float]] = []
    for wave in plan.waves:
        per_team: dict[str, float] = {}
        for uid in wave:
            for team, eff in c.by_id(uid).effort_by_team.items():
                per_team[team] = per_team.get(team, 0.0) + eff
        out.append(per_team)
    return out


def deterministic_end(estate: Estate, c: Contraction, plan: Plan, *, freeze: bool = True) -> float:
    t = 0.0
    for per_team in wave_team_effort(c, plan):
        if per_team:
            t += max(eff / estate.rate[team] for team, eff in per_team.items())
        if freeze:
            t = _out_of_freeze(t)
    return t


def simulate(
    estate: Estate,
    c: Contraction,
    plan: Plan,
    *,
    draws: int = 20_000,
    sigma: float = SIGMA,
    rho_programme: float = RHO_PROGRAMME,
    rho_wave: float = RHO_WAVE,
    seed: int = 7,
    freeze: bool = True,
) -> RiskResult:
    """Monte Carlo over per-team effort with a two-level correlation structure.

    ``z[w,t] = sqrt(a) G + sqrt(b) W_w + sqrt(1-a-b) E_wt`` gives correlation
    ``a + b`` between teams inside a wave and ``a`` between waves, which is the
    decomposition the report needs in order to separate the two mechanisms.
    Effort is ``median * exp(sigma * z)``: log-normal, so effort cannot go
    negative and the tail is on the right, where overruns live.
    """
    if rho_programme + rho_wave > 1.0:
        raise ValueError("correlation shares must sum to at most 1")

    rng = np.random.default_rng(seed)
    per_wave = wave_team_effort(c, plan)
    teams = sorted(estate.rate)
    n_w = len(per_wave)

    eff = np.zeros((n_w, len(teams)))
    for i, pw in enumerate(per_wave):
        for j, t in enumerate(teams):
            eff[i, j] = pw.get(t, 0.0)
    rates = np.array([estate.rate[t] for t in teams])

    a, b = rho_programme, rho_wave
    g = rng.standard_normal((draws, 1, 1))
    w = rng.standard_normal((draws, n_w, 1))
    e = rng.standard_normal((draws, n_w, len(teams)))
    z = math.sqrt(a) * g + math.sqrt(b) * w + math.sqrt(max(1.0 - a - b, 0.0)) * e

    # A log-normal with median m has mean m*exp(sigma^2/2); dividing by that
    # factor keeps the *mean* effort equal to the point estimate, so the
    # simulation is not simply adding a constant to every date. Without this
    # the merge bias would be inflated by a modelling choice.
    scale = np.exp(sigma * z - 0.5 * sigma * sigma)
    sampled = eff[None, :, :] * scale
    durations = np.max(np.where(eff[None, :, :] > 0, sampled / rates, 0.0), axis=2)

    if freeze:
        ends = np.zeros(draws)
        cum = np.zeros(draws)
        for i in range(n_w):
            cum = cum + durations[:, i]
            cum = np.array([_out_of_freeze(x) for x in cum])
        ends = cum
    else:
        ends = durations.sum(axis=1)

    return RiskResult(
        deterministic=deterministic_end(estate, c, plan, freeze=freeze),
        samples=np.sort(ends),
        rho_programme=a,
        rho_wave=b,
        sigma=sigma,
    )


@dataclass(frozen=True)
class WaveBias:
    wave: int
    teams: int
    #: Teams whose deterministic duration is within 20% of the wave's longest.
    #: These are the streams that can plausibly become the binding one under a
    #: draw. The mechanism says bias is driven by how many of these there are,
    #: not by how many teams are present at all.
    near_critical: int
    longest: float
    bias: float


def merge_bias_by_wave(
    estate: Estate, c: Contraction, plan: Plan, *, draws: int = 40_000, seed: int = 11
) -> list[WaveBias]:
    """Merge bias per wave, against team count and near-critical stream count.

    Reported side by side rather than asserted, because the obvious hypothesis
    -- bias grows with the number of teams -- and the mechanical one -- bias
    grows with the number of streams that could plausibly finish last -- make
    different predictions, and the waves in this plan separate them.
    """
    out: list[WaveBias] = []
    for i, pw in enumerate(wave_team_effort(c, plan)):
        active = {t: e for t, e in pw.items() if e > 0}
        if not active:
            continue
        # One generator per wave, seeded from the wave index, so a wave's
        # number does not depend on how many waves precede it.
        rng = np.random.default_rng(seed + i)
        durations = {t: e / estate.rate[t] for t, e in active.items()}
        det = max(durations.values())
        near = sum(1 for d in durations.values() if d >= 0.8 * det)
        z = rng.standard_normal((draws, len(active)))
        scale = np.exp(SIGMA * z - 0.5 * SIGMA * SIGMA)
        base = np.array([durations[t] for t in sorted(active)])
        sim = np.max(base[None, :] * scale, axis=1)
        out.append(
            WaveBias(
                wave=i + 1,
                teams=len(active),
                near_critical=near,
                longest=det,
                bias=float(np.mean(sim)) - det,
            )
        )
    return out


def cost_at_percentile(
    estate: Estate,
    c: Contraction,
    plan: Plan,
    result: RiskResult,
    *,
    remediation_cost: float,
    q: float,
) -> float:
    """Programme cost if the schedule lands at the q-th percentile.

    Overrun is charged where it is actually paid: the estate keeps running on
    premises for longer, and every hybrid link stays up for longer. Cloud spend
    does not scale with the overrun -- workloads that have cut over are already
    cloud-only -- so the marginal monthly rate is on-premises run plus live
    hybrid links, not the whole programme burn.
    """
    from .costs import EGRESS_PER_GB, evaluate

    base, sch = evaluate(estate, c, plan, remediation_cost=remediation_cost)
    slip = max(result.pct(q) - result.deterministic, 0.0)
    onprem_rate = sum(comp.onprem_monthly for comp in estate.components)
    link_rate = sum(
        d.hybrid_monthly + d.traffic_gb_mo * EGRESS_PER_GB
        for d in c.external
        if c.unit_of(d.source) != c.unit_of(d.target)
    )
    return base.total + slip * (onprem_rate + link_rate)


def rho_sweep(
    estate: Estate,
    c: Contraction,
    plan: Plan,
    grid: list[tuple[float, float]],
    *,
    draws: int = 12_000,
    freeze: bool = True,
) -> list[tuple[float, float, float, float, float, float]]:
    """P50, P90, standard deviation and merge bias across correlation settings.

    ``freeze`` is exposed because change-freeze slippage quantises the end
    date onto month boundaries, which compresses apparent differences between
    correlation settings. Sweeping with freezes off isolates the correlation
    effect; sweeping with them on is what the programme actually faces.
    """
    out: list[tuple[float, float, float, float, float, float]] = []
    for a, b in grid:
        r = simulate(
            estate,
            c,
            plan,
            draws=draws,
            rho_programme=a,
            rho_wave=b,
            freeze=freeze,
        )
        out.append(
            (a, b, r.pct(50), r.pct(90), float(np.std(r.samples)), r.merge_bias)
        )
    return out
