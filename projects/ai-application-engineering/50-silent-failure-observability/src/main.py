"""Entry point: run the panel, write the reports and the dashboard data.

    python -m src.main [--out docs]
"""

from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

from . import detectors, evaluate, report, stream


def dashboard_payload(panel: evaluate.PanelResult) -> dict:
    """Everything the TypeScript dashboard needs, and nothing it does not.

    The detector score series are normalised by their own thresholds before being written
    out, so a value of 1.0 means "at threshold" on every panel regardless of whether the
    underlying statistic is a PSI in the hundredths or an MMD in the millionths. Plotting
    raw scores on a shared axis would make the whole dashboard a chart of which statistic
    happens to have the larger units.
    """
    out: dict = {
        "days": stream.DAYS,
        "referenceDays": detectors.REFERENCE_DAYS,
        "persistence": detectors.PERSISTENCE,
        "alpha": detectors.ALPHA,
        "requestsPerDay": stream.REQUESTS_PER_DAY,
        "scenarios": [],
    }

    for scenario in stream.SCENARIOS:
        turns = list(evaluate._traffic(scenario.key))
        series = []
        for index in range(len(detectors.DETECTORS)):
            result = evaluate._run_detector(scenario.key, index)
            threshold = result.threshold if result.threshold > 0 else 1.0
            cell = evaluate.cell(panel, scenario.key, result.name)
            series.append({
                "detector": result.name,
                "notes": result.notes,
                "callsPerDay": result.calls_per_day,
                "normalised": [round(s / threshold, 4) for s in result.scores],
                "alertDay": cell.alert_day,
                "delayDays": cell.delay_days,
            })
        out["scenarios"].append({
            "key": scenario.key,
            "title": scenario.title,
            "story": scenario.story,
            "symptom": scenario.symptom,
            "isRegression": scenario.is_regression,
            "onsetDay": scenario.onset_day,
            "materialDay": panel.material_days[scenario.key],
            "quality": [round(q, 4) for q in stream.true_quality_series(turns)],
            "detectors": series,
        })

    out["coveringSet"] = list(evaluate.minimum_covering_set(panel))
    out["falseAlarmDays"] = panel.false_alarm_days
    out["inputShiftFalsePositive"] = panel.input_shift_false_positive
    return out


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Run the silent-failure detector panel.")
    parser.add_argument("--out", default="docs", help="directory for the generated reports")
    args = parser.parse_args(argv)

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)

    started = time.perf_counter()
    panel = evaluate.evaluate()
    elapsed = time.perf_counter() - started

    stable = report.render(panel)
    (out / "results-stable.md").write_text(stable, encoding="utf-8", newline="\n")
    (out / "results.md").write_text(
        report.render_full(panel, elapsed), encoding="utf-8", newline="\n"
    )
    (out / "dashboard-data.json").write_text(
        json.dumps(dashboard_payload(panel), indent=1, sort_keys=True) + "\n",
        encoding="utf-8",
        newline="\n",
    )

    chosen = evaluate.minimum_covering_set(panel)
    print(f"panel: {len(panel.detector_names)} detectors x {len(panel.scenario_keys)} scenarios")
    print(f"false-alarm days on healthy: {sum(panel.false_alarm_days.values())}")
    print(f"alerts on the non-regression: {sum(panel.input_shift_false_positive.values())}")
    print(f"cheapest covering set: {', '.join(chosen)}")
    print(f"  cost: {sum(evaluate.operating_cost(panel, d)[0] for d in chosen)} model calls/day")
    print(f"uncovered regressions: {evaluate.uncovered_regressions(panel) or 'none'}")
    print(f"wrote {out / 'results.md'}, {out / 'results-stable.md'}, {out / 'dashboard-data.json'} in {elapsed:.1f}s")
    return 0


if __name__ == "__main__":
    sys.exit(main())
