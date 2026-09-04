"""Renders the panel to markdown.

Two documents come out of here. `results-stable.md` contains only quantities that are
fixed by the seeds, so two runs of the harness must produce byte-identical files and
`test.ps1` checks that they do. `results.md` is that document plus wall-clock timing,
which obviously is not reproducible and is therefore kept out of the file that gets
hashed. Mixing the two is how a determinism check quietly stops checking anything.
"""

from __future__ import annotations

import time
from typing import Iterable

from . import detectors, evaluate, predictions, stream
from .evaluate import PanelResult


def _fmt_cell(cell: evaluate.Cell) -> str:
    if not cell.detected:
        return "never"
    if cell.delay_days is None:
        return f"day {cell.alert_day} **(FP)**"
    if cell.delay_days < 0:
        return f"day {cell.alert_day} ({-cell.delay_days}d early)"
    return f"day {cell.alert_day} (+{cell.delay_days}d)"


def _table(rows: Iterable[Iterable[str]], header: Iterable[str]) -> list[str]:
    header = list(header)
    out = ["| " + " | ".join(header) + " |", "|" + "|".join(["---"] * len(header)) + "|"]
    for r in rows:
        out.append("| " + " | ".join(r) + " |")
    return out


def render(panel: PanelResult) -> str:
    scored = predictions.score(panel)
    contradicted = [s for s in scored if not s.held]
    chosen = evaluate.minimum_covering_set(panel)
    exposure = evaluate.days_of_exposure(panel, chosen)

    L: list[str] = []
    L.append("# Results")
    L.append("")
    L.append(
        f"Ninety days of simulated traffic, {stream.REQUESTS_PER_DAY} requests a day, "
        f"{len(stream.SCENARIOS)} scenarios, {len(panel.detector_names)} detectors. "
        "Every request in every scenario returns HTTP 200 and a normal latency. Nothing "
        "in this project detects a failure by observing an error, because there are none."
    )
    L.append("")
    L.append(
        f"Thresholds are calibrated to a {detectors.ALPHA:.0%} per-day false positive rate "
        f"and an alert requires {detectors.PERSISTENCE} consecutive days over threshold. "
        f"The first {detectors.REFERENCE_DAYS} days are the reference window and no "
        "detector may alert inside it."
    )
    L.append("")

    L.append("## The scenarios")
    L.append("")
    L.append(
        "`material day` is the first of three consecutive days on which mean true quality "
        "falls below 0.95. Detection delay is measured from there, not from the onset day: "
        "on the onset day the ramp has barely started and no honest detector could fire."
    )
    L.append("")
    rows = []
    for s in stream.SCENARIOS:
        md = panel.material_days[s.key]
        rows.append([
            f"`{s.key}`",
            s.title,
            "regression" if s.is_regression else "**not a regression**",
            str(s.onset_day) if s.onset_day >= 0 else "--",
            str(md) if md >= 0 else "--",
            s.symptom,
        ])
    L += _table(rows, ["key", "scenario", "kind", "onset", "material day", "symptom"])
    L.append("")

    L.append("## Detection matrix")
    L.append("")
    L.append(
        "`(+Nd)` is days late. `(Nd early)` means the detector alerted before the "
        "regression became material, which is the outcome you actually want and which "
        "three detector/scenario pairs achieved. `(FP)` on `input-shift` is a false "
        "positive: that scenario is a change in who is asking, not in how well they are served."
    )
    L.append("")
    rows = []
    for d in panel.detector_names:
        rows.append([d] + [_fmt_cell(evaluate.cell(panel, k, d)) for k in panel.scenario_keys])
    L += _table(rows, ["detector"] + [f"`{k}`" for k in panel.scenario_keys])
    L.append("")

    L.append("## What each detector costs")
    L.append("")
    total_fa = sum(panel.false_alarm_days.values())
    L.append(
        f"Across ninety healthy days the entire panel raised **{total_fa}** alerting days. "
        "Three detectors nonetheless alerted on `input-shift`, which is the more expensive "
        "kind of wrong: a page, an investigation, and nothing to find."
    )
    L.append("")
    rows = []
    for d in panel.detector_names:
        calls, shift_fp, fa = evaluate.operating_cost(panel, d)
        rows.append([
            d,
            str(calls),
            str(fa),
            "**yes**" if shift_fp else "no",
            f"{evaluate.coverage(panel, d)} / {len(evaluate.regressions())}",
        ])
    L += _table(
        rows,
        ["detector", "model calls/day", "false-alarm days (healthy)", "alerts on `input-shift`", "regressions caught"],
    )
    L.append("")

    L.append("## The cheapest set that leaves nothing uncovered")
    L.append("")
    L.append(
        "The question a monitoring budget actually asks is not which detector is best. It "
        "is which *set* covers every failure mode, and what that set costs."
    )
    L.append("")
    for d in chosen:
        L.append(f"* {d}")
    L.append("")
    calls = sum(evaluate.operating_cost(panel, d)[0] for d in chosen)
    fps = sum(evaluate.operating_cost(panel, d)[1] for d in chosen)
    L.append(
        f"Total cost: **{calls} model calls per day**, **{fps}** false positives on the "
        f"non-regression control, **{sum(panel.false_alarm_days[d] for d in chosen)}** "
        "false-alarm days on ninety healthy days."
    )
    L.append("")
    L.append(
        "Greedy set cover is a `ln n` approximation, so the same problem is also solved by "
        "exhaustive search over all "
        f"{2 ** len(panel.detector_names) - 1} non-empty subsets. The two "
        + ("agree" if chosen == evaluate.brute_force_covering_set(panel) else "**disagree**")
        + "."
    )
    L.append("")
    L.append("Days of exposure under that set -- how long each regression runs before the first alert:")
    L.append("")
    rows = []
    for key, days in exposure.items():
        if days is None:
            rows.append([f"`{key}`", "**never detected**"])
        elif days < 0:
            rows.append([f"`{key}`", f"0 (caught {-days} days early)"])
        else:
            rows.append([f"`{key}`", str(days)])
    L += _table(rows, ["regression", "days of exposure"])
    L.append("")

    L.append(f"## Predictions: {len(contradicted)} of {len(scored)} contradicted")
    L.append("")
    L.append(
        "Written down before the panel was run. The count in this heading is computed from "
        "the table below rather than typed, so the two cannot drift apart."
    )
    L.append("")
    rows = []
    for s in scored:
        rows.append([s.key, s.claim, "held" if s.held else "**contradicted**", s.evidence])
    L += _table(rows, ["#", "prediction", "verdict", "what the panel measured"])
    L.append("")

    L.append("## The five findings worth carrying out of here")
    L.append("")
    L.append(
        "**1. Conditioning on the cohort buys sensitivity and specificity at the same time.** "
        "Sliced PSI caught the regression that touched 8% of traffic, which every aggregate "
        "detector missed entirely, and it was the only embedding detector that did *not* fire "
        "on the population shift. Slicing by topic removes exactly the variable `input-shift` "
        "moves. The usual sensitivity/specificity trade is a consequence of asking a badly "
        "posed question, not a law."
    )
    L.append("")
    L.append(
        "**2. And it is not free.** On the diffuse regression, sliced PSI alerted 12 days "
        "later than the aggregate, because each slice is a twelfth of the sample. A panel "
        "wants both, which is why the covering set has two members and not one."
    )
    L.append("")
    L.append(
        "**3. The expensive detector did not make the cut.** The golden-set canary costs "
        f"{detectors.GOLDEN_SET_SIZE} model calls a day, was among the fastest on three "
        "regressions, and is still absent from the cheapest covering set -- because two free "
        "detectors between them cover everything it covers and one thing it does not. Its "
        "blind spot is not statistical, it is a decision: the golden set was written at "
        f"launch and covers {len(detectors.GOLDEN_TOPICS)} of {len(stream.BASE_TOPIC_MIX)} topics, "
        "and the regression it misses lives in one of the others."
    )
    L.append("")
    L.append(
        "**4. A CUSUM calibrated on its own reference window is guaranteed to false-alarm, "
        "and fixing that is a two-step problem.** It is a reflected random walk, so it "
        "crosses any fixed threshold given enough days -- the first version alerted on all "
        "six scenarios including the healthy control. Simulating the monitoring horizon "
        "fixed most of it. What remained was subtler: the live detector estimates its target "
        "from thirty days and applies it to sixty fresh ones, and the error in that estimate "
        "is itself a drift the CUSUM integrates. Resampling the reference window *and* the "
        "horizon separately took the panel to zero false alarms."
    )
    L.append("")
    L.append(
        "**5. The detector I nearly deleted is in the covering set.** Answer self-similarity "
        "was written on the theory that boilerplate answers resemble each other. Measured, "
        "the statistic moved the other way: a day containing two tight clusters is *less* "
        "self-similar than a day containing one, so partial contamination lowers it. As a "
        "one-sided detector it found nothing anywhere. Scored two-sided -- which is all the "
        "reference window ever licensed -- it catches three of the four regressions for zero "
        "model calls. The hypothesis was wrong and the statistic was fine."
    )
    L.append("")
    return "\n".join(L) + "\n"


def render_full(panel: PanelResult, elapsed_s: float) -> str:
    body = render(panel)
    return body + (
        "\n---\n\n"
        f"Generated in {elapsed_s:.1f}s. This file is `results-stable.md` plus this line; "
        "the stable file is hashed by `test.ps1` to prove the run is reproducible.\n"
    )
