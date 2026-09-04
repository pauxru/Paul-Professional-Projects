"""Tests for the scoring panel, the covering-set logic and the generated reports.

These are the tests that hold the *conclusions* in place. If the covering set changes, or
a detector starts alerting on the non-regression control, the README and the essays are
wrong, and a test failure is the only thing that will say so.
"""

from __future__ import annotations

import json

import pytest

from src import detectors, evaluate, main, predictions, report, stream


@pytest.fixture(scope="module")
def panel():
    return evaluate.evaluate()


# ---------------------------------------------------------------------------
# shape


def test_panel_has_a_cell_for_every_pair(panel):
    assert len(panel.cells) == len(panel.detector_names) * len(panel.scenario_keys)


def test_panel_covers_every_scenario_and_detector(panel):
    assert set(panel.scenario_keys) == {s.key for s in stream.SCENARIOS}
    assert len(panel.detector_names) == len(detectors.DETECTORS)


def test_cell_lookup_raises_on_an_unknown_pair(panel):
    with pytest.raises(KeyError):
        evaluate.cell(panel, "no-such-scenario", panel.detector_names[0])


def test_evaluation_is_deterministic():
    a, b = evaluate.evaluate(), evaluate.evaluate()
    assert a.cells == b.cells


def test_delay_is_none_when_there_is_no_material_day(panel):
    for d in panel.detector_names:
        assert evaluate.cell(panel, "healthy", d).delay_days is None


def test_delay_is_the_gap_between_alert_and_materiality(panel):
    for c in panel.cells:
        if c.detected and c.material_day >= 0:
            assert c.delay_days == c.alert_day - c.material_day


# ---------------------------------------------------------------------------
# the headline claims


def test_the_panel_raises_no_false_alarms_on_ninety_healthy_days(panel):
    assert sum(panel.false_alarm_days.values()) == 0


def test_apm_catches_nothing(panel):
    """The premise. If APM ever catches one of these, the project has no subject."""
    assert evaluate.coverage(panel, "APM (error rate + p95 latency)") == 0


def test_every_regression_is_caught_by_something(panel):
    assert evaluate.uncovered_regressions(panel) == ()


def test_the_embedding_drift_detectors_cannot_tell_a_cohort_change_from_a_regression(panel):
    """The indictment of the default answer.

    PSI and MMD on embeddings are what "LLM drift detection" usually means. All three
    alert on a change in who is asking, which harmed nobody.
    """
    for name in ("PSI on output embeddings", "PSI on input embeddings", "MMD on output embeddings"):
        assert panel.input_shift_false_positive[name] is True, name


def test_slicing_by_cohort_is_immune_to_the_cohort_mix_changing(panel):
    """Conditioning on the topic removes exactly the variable `input-shift` moves."""
    assert panel.input_shift_false_positive["Sliced PSI (worst topic)"] is False


def test_only_slicing_catches_the_localised_regression(panel):
    """The 8%-of-traffic bug that every aggregate dilutes by a factor of twelve."""
    caught = [
        d for d in panel.detector_names
        if evaluate.cell(panel, "template-regression", d).detected
    ]
    assert "Sliced PSI (worst topic)" in caught
    assert "PSI on output embeddings" not in caught
    assert "Quality canary (golden set)" not in caught


def test_slicing_pays_for_that_with_sensitivity_on_a_diffuse_regression(panel):
    """Each slice is a twelfth of the sample, so the same detector is slower elsewhere."""
    sliced = evaluate.cell(panel, "model-swap", "Sliced PSI (worst topic)").delay_days
    aggregate = evaluate.cell(panel, "model-swap", "PSI on output embeddings").delay_days
    assert sliced is not None and aggregate is not None
    assert sliced > aggregate


def test_the_expensive_detector_is_not_in_the_cheapest_covering_set(panel):
    assert "Quality canary (golden set)" not in evaluate.minimum_covering_set(panel)


def test_the_covering_set_costs_no_model_calls(panel):
    chosen = evaluate.minimum_covering_set(panel)
    assert sum(evaluate.operating_cost(panel, d)[0] for d in chosen) == 0


def test_the_covering_set_raises_no_false_positives(panel):
    for d in evaluate.minimum_covering_set(panel):
        assert panel.false_alarm_days[d] == 0
        assert panel.input_shift_false_positive[d] is False


def test_the_covering_set_actually_covers(panel):
    chosen = evaluate.minimum_covering_set(panel)
    for s in evaluate.regressions():
        assert any(evaluate.cell(panel, s.key, d).detected for d in chosen), s.key


def test_minimum_covering_set_matches_brute_force(panel):
    """Greedy set cover is a `ln n` approximation. On a problem this small the exact
    answer is computable, so the approximation is checked rather than assumed."""
    assert evaluate.minimum_covering_set(panel) == evaluate.brute_force_covering_set(panel)


def test_covering_set_is_smaller_than_the_full_panel(panel):
    assert 0 < len(evaluate.minimum_covering_set(panel)) < len(panel.detector_names)


# ---------------------------------------------------------------------------
# cost model


def test_operating_cost_orders_money_before_false_positives(panel):
    canary = evaluate.operating_cost(panel, "Quality canary (golden set)")
    sliced = evaluate.operating_cost(panel, "Sliced PSI (worst topic)")
    assert canary[0] == detectors.GOLDEN_SET_SIZE
    assert sliced[0] == 0
    assert sliced < canary


def test_a_detector_that_alerts_on_the_control_costs_more_than_one_that_does_not(panel):
    assert evaluate.operating_cost(panel, "Sliced PSI (worst topic)") < evaluate.operating_cost(
        panel, "PSI on output embeddings"
    )


def test_days_of_exposure_takes_the_earliest_detector_in_the_set(panel):
    chosen = evaluate.minimum_covering_set(panel)
    exposure = evaluate.days_of_exposure(panel, chosen)
    for key, days in exposure.items():
        if days is None:
            continue
        best = min(
            evaluate.cell(panel, key, d).delay_days
            for d in chosen
            if evaluate.cell(panel, key, d).detected
        )
        assert days == best


def test_days_of_exposure_is_none_for_a_set_that_misses_something(panel):
    exposure = evaluate.days_of_exposure(panel, ("APM (error rate + p95 latency)",))
    assert all(v is None for v in exposure.values())


def test_regressions_excludes_the_controls():
    keys = {s.key for s in evaluate.regressions()}
    assert "healthy" not in keys and "input-shift" not in keys


def test_best_single_detector_is_not_enough_on_its_own(panel):
    """If one detector covered everything, the covering set would have one member."""
    _, best = evaluate.best_single_detector(panel)
    assert best < len(evaluate.regressions())


# ---------------------------------------------------------------------------
# predictions


def test_every_prediction_is_scored(panel):
    scored = predictions.score(panel)
    assert len(scored) == len(predictions.PREDICTIONS)
    assert all(isinstance(s.held, bool) for s in scored)
    assert all(s.evidence for s in scored)


def test_prediction_keys_are_unique():
    keys = [p.key for p in predictions.PREDICTIONS]
    assert len(keys) == len(set(keys))


def test_predictions_include_contradicted_ones(panel):
    """A scoreboard on which everything held is a scoreboard written after the fact."""
    scored = predictions.score(panel)
    assert any(not s.held for s in scored)
    assert any(s.held for s in scored)


def test_prediction_scoring_is_deterministic(panel):
    assert [s.held for s in predictions.score(panel)] == [s.held for s in predictions.score(panel)]


# ---------------------------------------------------------------------------
# report


def test_report_is_byte_identical_across_runs(panel):
    assert report.render(panel) == report.render(evaluate.evaluate())


def test_report_headline_count_matches_its_own_table(panel):
    """The count in the predictions heading is derived, not typed. This checks that the
    derivation and the table cannot disagree."""
    text = report.render(panel)
    contradicted = sum(1 for s in predictions.score(panel) if not s.held)
    assert f"## Predictions: {contradicted} of {len(predictions.PREDICTIONS)} contradicted" in text
    assert text.count("**contradicted**") == contradicted


def test_report_mentions_every_detector_and_scenario(panel):
    text = report.render(panel)
    for d in panel.detector_names:
        assert d in text
    for k in panel.scenario_keys:
        assert f"`{k}`" in text


def test_report_marks_false_positives_distinctly(panel):
    text = report.render(panel)
    assert "**(FP)**" in text


def test_report_reports_early_detections_as_early(panel):
    text = report.render(panel)
    assert "early)" in text


def test_full_report_is_the_stable_report_plus_timing(panel):
    stable = report.render(panel)
    full = report.render_full(panel, 12.3)
    assert full.startswith(stable)
    assert "12.3s" in full
    assert "12.3s" not in stable


def test_dashboard_payload_normalises_by_threshold(panel):
    """Every series is divided by its own threshold, so 1.0 means "at threshold" on every
    chart. Scores may be negative -- APM's p95 sits below its reference and the canary's
    inverted score goes negative on a good day -- which is why the dashboard clamps its
    axis at zero rather than assuming otherwise."""
    payload = main.dashboard_payload(panel)
    for scenario in payload["scenarios"]:
        for series in scenario["detectors"]:
            assert len(series["normalised"]) == stream.DAYS
            assert all(isinstance(v, float) and v == v for v in series["normalised"])

    by_name = {
        s["detector"]: s
        for s in next(x for x in payload["scenarios"] if x["key"] == "healthy")["detectors"]
    }
    # The alert rule expressed directly against the dashboard's own numbers: on the healthy
    # stream no detector may have `PERSISTENCE` consecutive days above 1.0 after the
    # reference window. If the page ever shows one, the page and the report disagree.
    for name, series in by_name.items():
        run = 0
        for value in series["normalised"][detectors.REFERENCE_DAYS:]:
            run = run + 1 if value > 1.0 else 0
            assert run < detectors.PERSISTENCE, name
    assert any(min(s["normalised"]) < 0 for s in by_name.values())


def test_dashboard_payload_is_json_serialisable(panel):
    assert json.loads(json.dumps(main.dashboard_payload(panel)))


def test_dashboard_payload_carries_the_ground_truth_for_the_top_chart(panel):
    payload = main.dashboard_payload(panel)
    by_key = {s["key"]: s for s in payload["scenarios"]}
    assert all(q == 1.0 for q in by_key["healthy"]["quality"])
    assert min(by_key["model-swap"]["quality"]) < 0.8


def test_dashboard_payload_alert_days_agree_with_the_panel(panel):
    payload = main.dashboard_payload(panel)
    for scenario in payload["scenarios"]:
        for series in scenario["detectors"]:
            assert series["alertDay"] == evaluate.cell(panel, scenario["key"], series["detector"]).alert_day


def test_main_writes_all_three_artefacts(tmp_path):
    assert main.main(["--out", str(tmp_path)]) == 0
    for name in ("results.md", "results-stable.md", "dashboard-data.json"):
        assert (tmp_path / name).read_text(encoding="utf-8")
