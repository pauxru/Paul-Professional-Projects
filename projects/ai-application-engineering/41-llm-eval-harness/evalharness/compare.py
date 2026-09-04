"""Turning two eval runs into a decision.

The gate is the part that has to be defensible at 5pm on a Friday when someone
wants to ship. Everything upstream is measurement; this is where measurement
becomes a yes or a no, and where the temptation to shade a threshold is
strongest.

Two design commitments:

**Asymmetry.** Shipping a regression and blocking an improvement are not
equally bad, so they do not get the same evidential standard. A regression is
blocked on *any* credible evidence of harm; an improvement is only *claimed*
when the interval excludes zero. In between is a wide band where the honest
answer is "this eval cannot tell", and the gate says so rather than picking a
side from the point estimate.

**No aggregate-only verdicts.** A change that lifts easy items and breaks
adversarial ones nets to zero and passes an aggregate gate. Slices are checked
independently, with the multiplicity that implies handled explicitly.
"""

from __future__ import annotations

from dataclasses import dataclass
from enum import Enum

import numpy as np

from .dataset import Dataset, TIERS
from .stats import (Interval, benjamini_hochberg, minimum_detectable_effect,
                    paired_bca_bootstrap, paired_permutation_test)
from .systems import RunResult


class Verdict(str, Enum):
    IMPROVED = "IMPROVED"
    REGRESSED = "REGRESSED"
    INDISTINGUISHABLE = "INDISTINGUISHABLE"
    UNDERPOWERED = "UNDERPOWERED"
    INCOMPARABLE = "INCOMPARABLE"


@dataclass(frozen=True)
class SliceResult:
    name: str
    n: int
    baseline_mean: float
    candidate_mean: float
    interval: Interval
    p_value: float
    significant: bool = False
    """Set by the multiplicity correction across slices, not by p < 0.05."""

    @property
    def delta(self) -> float:
        return self.candidate_mean - self.baseline_mean


@dataclass(frozen=True)
class Comparison:
    """Everything needed to decide, and to explain the decision afterwards."""

    baseline: str
    candidate: str
    n: int
    verdict: Verdict
    reason: str
    overall: SliceResult
    slices: tuple[SliceResult, ...]
    mde: float
    """Minimum effect this eval set could detect at 80% power."""

    cost_delta_usd: float
    p95_latency_delta_ms: float

    @property
    def blocked(self) -> bool:
        return self.verdict in (Verdict.REGRESSED, Verdict.INCOMPARABLE)

    def summary(self) -> str:
        lines = [
            f"{self.candidate} vs {self.baseline}  [{self.verdict.value}]",
            f"  {self.reason}",
            f"  overall  {self.overall.baseline_mean:.4f} -> {self.overall.candidate_mean:.4f}  "
            f"{self.overall.interval}",
            f"  n={self.n}  detectable effect at 80% power: {self.mde:.4f}",
            f"  cost {self.cost_delta_usd:+.4f} USD   P95 latency {self.p95_latency_delta_ms:+.0f} ms",
        ]
        for s in self.slices:
            mark = "*" if s.significant else " "
            lines.append(f"  {mark} {s.name:<12} n={s.n:<4} {s.interval}")
        return "\n".join(lines)


def compare(
    baseline: RunResult,
    candidate: RunResult,
    dataset: Dataset,
    *,
    level: float = 0.95,
    resamples: int = 10_000,
    seed: int = 0,
    regression_tolerance: float = 0.0,
    allow_dataset_mismatch: bool = False,
) -> Comparison:
    """Compare two runs and produce a gate decision.

    `regression_tolerance` is the amount of measured decline that is accepted
    without blocking. It defaults to zero, which combined with the
    interval-based rule below means: block if the interval's *lower* bound is
    below -tolerance, i.e. if the data are consistent with a regression larger
    than we are willing to accept.
    """
    if baseline.item_ids != candidate.item_ids:
        return _incomparable(baseline, candidate,
                             "runs cover different items, so nothing can be paired")
    if (baseline.dataset_fingerprint != candidate.dataset_fingerprint
            and not allow_dataset_mismatch):
        return _incomparable(
            baseline, candidate,
            f"dataset fingerprint differs ({baseline.dataset_fingerprint[:12]} vs "
            f"{candidate.dataset_fingerprint[:12]}): the instrument changed between runs")

    overall_ci = paired_bca_bootstrap(baseline.scores, candidate.scores,
                                      level=level, resamples=resamples, seed=seed)
    overall_p = paired_permutation_test(baseline.scores, candidate.scores,
                                        resamples=resamples, seed=seed)
    overall = SliceResult("overall", baseline.scores.size,
                          baseline.mean_score, candidate.mean_score,
                          overall_ci, overall_p)

    raw_slices: list[SliceResult] = []
    for tier in TIERS:
        b = baseline.slice_scores(dataset, tier)
        c = candidate.slice_scores(dataset, tier)
        # A two-item slice produces an interval so wide it is not evidence of
        # anything; reporting it invites someone to read the point estimate.
        if b.size < 5:
            continue
        ci = paired_bca_bootstrap(b, c, level=level, resamples=resamples, seed=seed)
        p = paired_permutation_test(b, c, resamples=resamples, seed=seed)
        raw_slices.append(SliceResult(tier, b.size, float(b.mean()), float(c.mean()), ci, p))

    # Slices are multiple tests against the same change. Without correction,
    # four slices at alpha=0.05 give an 18.5% chance of at least one spurious
    # "significant" slice, and a spurious slice regression is exactly the kind
    # of finding that gets a good change blocked and a team's trust in the gate
    # burned.
    flags = benjamini_hochberg([s.p_value for s in raw_slices], fdr=0.05)
    slices = tuple(
        SliceResult(s.name, s.n, s.baseline_mean, s.candidate_mean, s.interval,
                    s.p_value, significant=f)
        for s, f in zip(raw_slices, flags)
    )

    sd = float(np.std(candidate.scores - baseline.scores, ddof=1))
    mde = minimum_detectable_effect(baseline.scores.size, sd) if sd > 0 else 0.0

    verdict, reason = _decide(overall, slices, mde, regression_tolerance)

    return Comparison(
        baseline=baseline.model,
        candidate=candidate.model,
        n=baseline.scores.size,
        verdict=verdict,
        reason=reason,
        overall=overall,
        slices=slices,
        mde=mde,
        cost_delta_usd=candidate.cost_usd() - baseline.cost_usd(),
        p95_latency_delta_ms=candidate.p95_latency - baseline.p95_latency,
    )


def _decide(
    overall: SliceResult,
    slices: tuple[SliceResult, ...],
    mde: float,
    tolerance: float,
) -> tuple[Verdict, str]:
    """The decision rule, in order of precedence.

    Written as a single readable cascade rather than spread across the caller,
    because a gate rule that has to be reconstructed from three places is a
    gate rule that will be argued with.
    """
    # 1. Any slice with credible evidence of harm blocks, even if the aggregate
    #    is flat. This is the case the aggregate is structurally blind to.
    for s in slices:
        if s.significant and s.interval.high < -tolerance:
            return (Verdict.REGRESSED,
                    f"the {s.name} slice regressed by {s.delta:+.4f} "
                    f"(CI {s.interval.low:+.4f}..{s.interval.high:+.4f}), which the "
                    f"overall mean of {overall.delta:+.4f} hides")

    # 2. Credible evidence of overall harm blocks.
    if overall.interval.high < -tolerance:
        return (Verdict.REGRESSED,
                f"the whole interval is below the tolerance of {-tolerance:+.4f}")

    # 3. Credible evidence of overall benefit is a pass with a claim.
    if overall.interval.low > 0.0:
        return (Verdict.IMPROVED,
                f"the interval excludes zero, so the improvement of "
                f"{overall.delta:+.4f} survives resampling")

    # 4. Everything else is a pass without a claim -- and the distinction
    #    between "we looked and there is nothing" and "we could not have seen
    #    it" is the one this whole harness exists to draw.
    if abs(overall.delta) < mde:
        return (Verdict.UNDERPOWERED,
                f"the observed change of {overall.delta:+.4f} is smaller than the "
                f"{mde:.4f} this eval set can detect at 80% power. This is not "
                f"evidence of no change; it is an absence of evidence either way")
    return (Verdict.INDISTINGUISHABLE,
            f"the interval {overall.interval.low:+.4f}..{overall.interval.high:+.4f} "
            f"straddles zero")


def _incomparable(baseline: RunResult, candidate: RunResult, why: str) -> Comparison:
    empty = Interval(0.0, 0.0, 0.0, 0.95, "none")
    return Comparison(
        baseline=baseline.model, candidate=candidate.model, n=0,
        verdict=Verdict.INCOMPARABLE, reason=why,
        overall=SliceResult("overall", 0, baseline.mean_score, candidate.mean_score, empty, 1.0),
        slices=(), mde=float("nan"), cost_delta_usd=0.0, p95_latency_delta_ms=0.0,
    )
