"""Scoring the panel.

For each (scenario, detector) pair: did it alert, and how many days after the degradation
became material? Plus, on the healthy control stream, how often did it alert when nothing
was happening?

Detection delay is measured from the first day mean true quality falls below 0.95, not
from the onset day. On the onset day the ramp has barely started and no honest detector
could fire; measuring from there would add a constant to every row and make the numbers
look like something they are not.
"""

from __future__ import annotations

from dataclasses import dataclass
from functools import lru_cache
from itertools import combinations

from . import detectors, stream
from .corpus import Turn
from .stream import SCENARIOS, Scenario


@dataclass(frozen=True)
class Cell:
    scenario: str
    detector: str
    #: Day the alert fired, or -1.
    alert_day: int
    #: Day the degradation became material, or -1 for the healthy control.
    material_day: int
    #: alert_day - material_day, or None if never detected.
    delay_days: int | None
    calls_per_day: int

    @property
    def detected(self) -> bool:
        return self.alert_day >= 0


@dataclass(frozen=True)
class PanelResult:
    cells: tuple[Cell, ...]
    #: detector name -> number of alerting days on the healthy control stream.
    false_alarm_days: dict[str, int]
    #: detector name -> whether it alerted on the input-shift stream (a false positive).
    input_shift_false_positive: dict[str, bool]
    detector_names: tuple[str, ...]
    scenario_keys: tuple[str, ...]
    material_days: dict[str, int]


@lru_cache(maxsize=None)
def _traffic(scenario_key: str) -> tuple[Turn, ...]:
    return tuple(stream.generate(scenario_key))


@lru_cache(maxsize=None)
def _run_detector(scenario_key: str, index: int) -> detectors.DetectorResult:
    turns = list(_traffic(scenario_key))
    return detectors.DETECTORS[index](turns, stream.DAYS)


def evaluate() -> PanelResult:
    cells: list[Cell] = []
    false_alarms: dict[str, int] = {}
    shift_fp: dict[str, bool] = {}
    names: list[str] = []
    material: dict[str, int] = {}

    for scenario in SCENARIOS:
        turns = list(_traffic(scenario.key))
        material_day = stream.first_materially_degraded_day(turns)
        material[scenario.key] = material_day

        for index in range(len(detectors.DETECTORS)):
            result = _run_detector(scenario.key, index)
            if result.name not in names:
                names.append(result.name)

            day = detectors.first_alert(result.scores, result.threshold)
            delay = None
            if day >= 0 and material_day >= 0:
                delay = day - material_day

            cells.append(
                Cell(
                    scenario=scenario.key,
                    detector=result.name,
                    alert_day=day,
                    material_day=material_day,
                    delay_days=delay,
                    calls_per_day=result.calls_per_day,
                )
            )

            if scenario.key == "healthy":
                false_alarms[result.name] = len(
                    detectors.alert_days(result.scores, result.threshold)
                )
            if scenario.key == "input-shift":
                shift_fp[result.name] = day >= 0

    return PanelResult(
        cells=tuple(cells),
        false_alarm_days=false_alarms,
        input_shift_false_positive=shift_fp,
        detector_names=tuple(names),
        scenario_keys=tuple(s.key for s in SCENARIOS),
        material_days=material,
    )


def cell(panel: PanelResult, scenario: str, detector: str) -> Cell:
    for c in panel.cells:
        if c.scenario == scenario and c.detector == detector:
            return c
    raise KeyError(f"{scenario} / {detector}")


def regressions() -> tuple[Scenario, ...]:
    """The scenarios that are genuine quality regressions -- what a detector should catch."""
    return tuple(s for s in SCENARIOS if s.is_regression)


def coverage(panel: PanelResult, detector: str) -> int:
    """How many of the genuine regressions this detector catches."""
    return sum(1 for s in regressions() if cell(panel, s.key, detector).detected)


def best_single_detector(panel: PanelResult) -> tuple[str, int]:
    scored = [(coverage(panel, d), d) for d in panel.detector_names]
    scored.sort(key=lambda x: (-x[0], x[1]))
    return scored[0][1], scored[0][0]


def operating_cost(panel: PanelResult, detector: str) -> tuple[int, int, int]:
    """What running this detector costs, in the order the costs actually bite.

    Model calls per day are money. An alert on the `input-shift` control is a page raised
    for a population change that harmed nobody, and it is the expensive kind of wrong:
    the team investigates, finds nothing, and trusts the next alert less. False-alarm days
    on the healthy stream are the same currency in smaller denominations.
    """
    costs = {c.detector: c.calls_per_day for c in panel.cells}
    return (
        costs.get(detector, 0),
        1 if panel.input_shift_false_positive.get(detector, False) else 0,
        panel.false_alarm_days.get(detector, 0),
    )


def minimum_covering_set(panel: PanelResult) -> tuple[str, ...]:
    """Smallest set of detectors covering every genuine regression.

    Greedy set cover, tie-broken by `operating_cost` and then by name so the answer is
    deterministic. This is the number that actually goes in a budget request: not "which
    detector is best" but "what is the cheapest set that leaves nothing uncovered".

    Greedy is not optimal for set cover in general -- it is within a `ln n` factor. With
    five regressions and eight detectors the exact answer is checkable by brute force, and
    `test_minimum_covering_set_matches_brute_force` does exactly that rather than trusting
    the approximation.
    """
    uncovered = {s.key for s in regressions()}
    chosen: list[str] = []

    while uncovered:
        best: tuple[int, tuple[int, int, int], str] | None = None
        for detector in panel.detector_names:
            gain = sum(1 for k in uncovered if cell(panel, k, detector).detected)
            if gain == 0:
                continue
            candidate = (-gain, operating_cost(panel, detector), detector)
            if best is None or candidate < best:
                best = candidate
        if best is None:
            break
        chosen.append(best[2])
        uncovered -= {k for k in uncovered if cell(panel, k, best[2]).detected}

    return tuple(chosen)


def brute_force_covering_set(panel: PanelResult) -> tuple[str, ...]:
    """Exact minimum covering set by exhaustive search, for checking the greedy one."""
    coverable = {
        s.key for s in regressions()
        if any(cell(panel, s.key, d).detected for d in panel.detector_names)
    }
    names = panel.detector_names
    for size in range(1, len(names) + 1):
        best: tuple[tuple[int, int, int], tuple[str, ...]] | None = None
        for combo in combinations(names, size):
            covered = {
                k for k in coverable
                if any(cell(panel, k, d).detected for d in combo)
            }
            if covered != coverable:
                continue
            totals = [operating_cost(panel, d) for d in combo]
            candidate = (
                (
                    sum(t[0] for t in totals),
                    sum(t[1] for t in totals),
                    sum(t[2] for t in totals),
                ),
                combo,
            )
            if best is None or candidate < best:
                best = candidate
        if best is not None:
            return best[1]
    return ()


def uncovered_regressions(panel: PanelResult) -> tuple[str, ...]:
    """Regressions no detector in the panel catches at all."""
    out = []
    for s in regressions():
        if not any(cell(panel, s.key, d).detected for d in panel.detector_names):
            out.append(s.key)
    return tuple(out)


def days_of_exposure(panel: PanelResult, detectors_in_use: tuple[str, ...]) -> dict[str, int | None]:
    """For each regression, how long it runs before the *first* detector in the set fires."""
    out: dict[str, int | None] = {}
    for s in regressions():
        delays = [
            cell(panel, s.key, d).delay_days
            for d in detectors_in_use
            if cell(panel, s.key, d).detected
        ]
        delays = [d for d in delays if d is not None]
        out[s.key] = min(delays) if delays else None
    return out
