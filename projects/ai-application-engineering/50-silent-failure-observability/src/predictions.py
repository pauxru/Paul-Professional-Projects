"""Predictions recorded at design time, scored against the panel.

These were written down before the detectors were run, and several of them are wrong in
ways the code itself now records: the docstring on `answer_diversity` explains why its
founding hypothesis was backwards, and the one on `bootstrap_cusum_threshold` explains
two successive failures of a threshold I expected to work first time. The point of
writing predictions down is that being wrong becomes a finding instead of a quiet edit.

Each prediction is scored by a function that reads the panel. Nothing here is hand-typed
from a previous run, so the scoreboard cannot drift away from the numbers it describes.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Callable

import numpy as np

from . import detectors, evaluate
from .evaluate import PanelResult

APM = "APM (error rate + p95 latency)"
PSI_OUT = "PSI on output embeddings"
PSI_IN = "PSI on input embeddings"
MMD = "MMD on output embeddings"
CUSUM = "Refusal-rate CUSUM"
SELFSIM = "Answer self-similarity"
SLICED = "Sliced PSI (worst topic)"
CANARY = "Quality canary (golden set)"


@dataclass(frozen=True)
class Prediction:
    key: str
    claim: str
    check: Callable[[PanelResult], tuple[bool, str]]


def _detected(panel: PanelResult, detector: str, scenario: str) -> bool:
    return evaluate.cell(panel, scenario, detector).detected


def _delay(panel: PanelResult, detector: str, scenario: str) -> int | None:
    return evaluate.cell(panel, scenario, detector).delay_days


# ---------------------------------------------------------------------------


def _p01(panel: PanelResult) -> tuple[bool, str]:
    caught = [s.key for s in evaluate.regressions() if _detected(panel, APM, s.key)]
    return not caught, f"APM caught {len(caught)} of {len(evaluate.regressions())} regressions"


def _p02(panel: PanelResult) -> tuple[bool, str]:
    ok = _detected(panel, PSI_IN, "input-shift") and not any(
        _detected(panel, PSI_IN, s.key) for s in evaluate.regressions()
    )
    return ok, "input PSI fires on the population shift and on nothing else"


def _p03(panel: PanelResult) -> tuple[bool, str]:
    fps = [d for d, v in panel.input_shift_false_positive.items() if v]
    return len(fps) == 0, f"{len(fps)} of {len(panel.detector_names)} alerted on the non-regression: {', '.join(fps) or 'none'}"


def _p04(panel: PanelResult) -> tuple[bool, str]:
    return _detected(panel, PSI_OUT, "template-regression"), (
        "aggregate output PSI on the 8%-of-traffic regression: "
        + ("alerted" if _detected(panel, PSI_OUT, "template-regression") else "never alerted")
    )


def _p05(panel: PanelResult) -> tuple[bool, str]:
    ok = _detected(panel, SLICED, "template-regression") and not _detected(
        panel, SLICED, "input-shift"
    )
    return ok, "slicing by topic caught the localised regression without alerting on the mix shift"


def _p06(panel: PanelResult) -> tuple[bool, str]:
    ms_agg = _delay(panel, PSI_OUT, "model-swap")
    ms_sl = _delay(panel, SLICED, "model-swap")
    if ms_agg is None or ms_sl is None:
        return False, "one of the two never alerted"
    return ms_sl > ms_agg, f"on the diffuse regression sliced PSI took {ms_sl} days vs aggregate {ms_agg}"


def _p07(panel: PanelResult) -> tuple[bool, str]:
    return CANARY in evaluate.minimum_covering_set(panel), (
        "cheapest covering set = " + ", ".join(evaluate.minimum_covering_set(panel))
    )


def _p08(panel: PanelResult) -> tuple[bool, str]:
    chosen = evaluate.minimum_covering_set(panel)
    calls = sum(evaluate.operating_cost(panel, d)[0] for d in chosen)
    return calls > 0, f"the covering set costs {calls} model calls per day"


def _p09(panel: PanelResult) -> tuple[bool, str]:
    return _detected(panel, SELFSIM, "model-swap"), (
        "self-similarity on the boilerplate collapse: "
        + ("alerted" if _detected(panel, SELFSIM, "model-swap") else "never alerted")
    )


def _p16(panel: PanelResult) -> tuple[bool, str]:
    """Did the day's mean pairwise answer similarity go *up* under boilerplate collapse?"""
    from . import embedding, stream

    turns = stream.generate("model-swap")
    buckets = stream.by_day(turns)
    ref = float(
        np.mean([
            embedding.mean_pairwise_cosine(embedding.embed_all([t.answer for t in b]))
            for b in buckets[:detectors.REFERENCE_DAYS]
        ])
    )
    late = float(
        np.mean([
            embedding.mean_pairwise_cosine(embedding.embed_all([t.answer for t in b]))
            for b in buckets[-10:]
        ])
    )
    return late > ref, (
        f"mean pairwise similarity moved from {ref:.4f} in the healthy window to "
        f"{late:.4f} once 60% of traffic was boilerplate ({late - ref:+.4f})"
    )


def _p10(panel: PanelResult) -> tuple[bool, str]:
    mmd_hits = sum(1 for s in evaluate.regressions() if _detected(panel, MMD, s.key))
    psi_hits = sum(1 for s in evaluate.regressions() if _detected(panel, PSI_OUT, s.key))
    return mmd_hits > psi_hits, f"MMD caught {mmd_hits} regressions, marginal PSI caught {psi_hits}"


def _p11(panel: PanelResult) -> tuple[bool, str]:
    total = sum(panel.false_alarm_days.values())
    return total == 0, f"{total} alerting days across the whole panel on ninety healthy days"


def _p12(panel: PanelResult) -> tuple[bool, str]:
    early = [
        (s.key, d)
        for s in evaluate.regressions()
        for d in panel.detector_names
        if (_delay(panel, d, s.key) or 0) < 0 and _detected(panel, d, s.key)
    ]
    return not early, f"{len(early)} detector/scenario pairs alerted before quality became material"


def _p13(panel: PanelResult) -> tuple[bool, str]:
    uncovered = evaluate.uncovered_regressions(panel)
    return bool(uncovered), f"regressions no detector caught: {', '.join(uncovered) or 'none'}"


def _p14(panel: PanelResult) -> tuple[bool, str]:
    greedy = evaluate.minimum_covering_set(panel)
    exact = evaluate.brute_force_covering_set(panel)
    return greedy != exact, (
        "greedy set cover "
        + ("disagreed with" if greedy != exact else "agreed with")
        + " exhaustive search"
    )


def _p15(panel: PanelResult) -> tuple[bool, str]:
    delays = [
        _delay(panel, d, "retrieval-decay")
        for d in panel.detector_names
        if _detected(panel, d, "retrieval-decay")
    ]
    best = min([x for x in delays if x is not None], default=None)
    others = []
    for s in evaluate.regressions():
        if s.key == "retrieval-decay":
            continue
        ds = [_delay(panel, d, s.key) for d in panel.detector_names if _detected(panel, d, s.key)]
        ds = [x for x in ds if x is not None]
        if ds:
            others.append(min(ds))
    ok = best is not None and all(best > o for o in others)
    return ok, f"best delay on retrieval decay was {best} days; best on every other regression was {sorted(others)}"


PREDICTIONS: tuple[Prediction, ...] = (
    Prediction("P01", "APM -- error rate and p95 latency -- will not catch a single one of the four regressions.", _p01),
    Prediction("P02", "Input-side PSI will fire on the population shift and on no genuine regression, because it is watching the wrong side of the system.", _p02),
    Prediction("P03", "No detector will alert on `input-shift`, since a change in who is asking is not a change in how well they are served.", _p03),
    Prediction("P04", "Aggregate output PSI will catch the template regression, because a truncated answer is a large change in a small share of traffic.", _p04),
    Prediction("P05", "Slicing PSI by topic will catch the localised regression that the aggregate misses, and will not alert on the mix shift.", _p05),
    Prediction("P06", "Slicing will be *slower* than aggregating on a diffuse regression, because each slice is a twelfth of the sample.", _p06),
    Prediction("P07", "The golden-set quality canary will be in the cheapest covering set. It is the only detector that looks at quality directly.", _p07),
    Prediction("P08", "The cheapest covering set will cost model calls per day, because free detectors will not be enough.", _p08),
    Prediction("P09", "Answer self-similarity will catch the model swap.", _p09),
    Prediction("P10", "MMD will beat marginal PSI, because it tests the joint distribution and PSI only tests the margins.", _p10),
    Prediction("P11", "With every threshold calibrated to a 1% per-day false positive rate, the panel will produce zero alerting days on ninety healthy days.", _p11),
    Prediction("P12", "No detector will alert before the regression becomes material. Detection is a lagging measurement by construction.", _p12),
    Prediction("P13", "At least one regression will go completely undetected by the whole panel.", _p13),
    Prediction("P14", "Greedy set cover will disagree with exhaustive search on a problem this small.", _p14),
    Prediction("P15", "Retrieval decay -- fluent, confident, wrong -- will be the slowest regression to detect, because nothing about the text looks broken.", _p15),
    Prediction("P16", "Answer self-similarity will *rise* when a share of traffic collapses into boilerplate, because boilerplate answers resemble each other.", _p16),
)


@dataclass(frozen=True)
class Scored:
    key: str
    claim: str
    held: bool
    evidence: str


def score(panel: PanelResult) -> tuple[Scored, ...]:
    out = []
    for p in PREDICTIONS:
        held, evidence = p.check(panel)
        out.append(Scored(key=p.key, claim=p.claim, held=held, evidence=evidence))
    return tuple(out)
