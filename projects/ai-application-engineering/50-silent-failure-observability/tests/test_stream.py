"""Tests for the traffic simulator and the ground truth it produces.

The simulator is the load-bearing part of this project: if it injects degradations in a
way that a detector can see for the wrong reason, every number in the report is a
measurement of the simulator rather than of the detectors. Most of these tests exist to
pin down that boundary.
"""

from __future__ import annotations

import pytest

from src import corpus, stream


def test_generates_the_expected_volume():
    turns = stream.generate("healthy")
    assert len(turns) == stream.DAYS * stream.REQUESTS_PER_DAY


def test_every_request_succeeds_in_every_scenario():
    """The premise of the entire project. If anything ever returns a non-200 then APM
    would catch it and there is nothing here worth measuring."""
    for scenario in stream.SCENARIOS:
        turns = stream.generate(scenario.key)
        assert all(t.http_status == 200 for t in turns), scenario.key


def test_latency_is_not_a_signal():
    """A cheaper or more heavily quantised model is usually *faster*. If the simulator
    made degraded turns slower, the whole panel would collapse to a latency alert."""
    turns = stream.generate("model-swap")
    late = [t for t in turns if t.day >= 80]
    degraded = [t.latency_ms for t in late if t.is_degraded]
    healthy = [t.latency_ms for t in late if not t.is_degraded]
    assert degraded and healthy
    mean_d = sum(degraded) / len(degraded)
    mean_h = sum(healthy) / len(healthy)
    assert abs(mean_d - mean_h) < 40.0


def test_generation_is_deterministic():
    a = stream.generate("model-swap")
    b = stream.generate("model-swap")
    assert [t.answer for t in a] == [t.answer for t in b]


def test_different_seeds_produce_different_traffic():
    a = stream.generate("healthy", seed=1)
    b = stream.generate("healthy", seed=2)
    assert [t.answer for t in a] != [t.answer for t in b]


def test_healthy_stream_has_no_degradation_at_all():
    turns = stream.generate("healthy")
    assert not any(t.is_degraded for t in turns)
    assert all(q == 1.0 for q in stream.true_quality_series(turns))


def test_healthy_stream_has_no_material_day():
    assert stream.first_materially_degraded_day(stream.generate("healthy")) == -1


def test_input_shift_is_not_a_regression():
    """`input-shift` is the control for false positives. If it carries any ground-truth
    quality loss then a detector alerting on it is right, and the panel has no control.

    An earlier version of the simulator gated the refusal-to-degradation branch on
    severity alone rather than on the scenario, which gave every ramping scenario -- this
    one included -- a real quality drop. The panel looked fine and was measuring nothing.
    """
    turns = stream.generate("input-shift")
    assert not any(t.is_degraded for t in turns)
    assert stream.first_materially_degraded_day(turns) == -1
    assert not stream.SCENARIOS_BY_KEY["input-shift"].is_regression


def test_input_shift_actually_shifts_the_topic_mix():
    turns = stream.generate("input-shift")
    early = [t for t in turns if t.day < 20]
    late = [t for t in turns if t.day >= 70]
    frac = lambda ts: sum(1 for t in ts if t.topic == "technical") / len(ts)
    assert frac(late) > frac(early) + 0.1


def test_every_genuine_regression_has_a_material_day():
    for scenario in stream.SCENARIOS:
        if not scenario.is_regression:
            continue
        day = stream.first_materially_degraded_day(stream.generate(scenario.key))
        assert day > scenario.onset_day, scenario.key


def test_material_day_requires_persistence():
    """One noisy day must not decide the ground truth every detection delay is measured
    against.

    The localised regression is the case that exposes this: its full-severity mean quality
    sits near the 0.95 line, so single days cross it early by chance. With persistence=1
    the material day is 52; with the three-day rule every detector also pays, it is 57.
    Five days of difference in a number that every detection delay is subtracted from.
    """
    turns = stream.generate("template-regression")
    assert stream.first_materially_degraded_day(turns, persistence=1) == 52
    assert stream.first_materially_degraded_day(turns, persistence=3) == 57


def test_material_day_is_stable_for_a_decisive_regression():
    """Where the effect is large the persistence rule costs nothing, which is the point:
    it only moves the answer where the answer was noise."""
    turns = stream.generate("model-swap")
    assert stream.first_materially_degraded_day(turns, persistence=1) == \
        stream.first_materially_degraded_day(turns, persistence=3)


def test_material_day_is_after_onset_for_a_ramped_degradation():
    turns = stream.generate("model-swap")
    onset = stream.SCENARIOS_BY_KEY["model-swap"].onset_day
    assert stream.first_materially_degraded_day(turns) > onset


def test_ramp_is_zero_before_onset_and_saturates_after():
    assert stream._ramp(10, 45) == 0.0
    assert stream._ramp(44, 45) == 0.0
    assert 0.0 < stream._ramp(48, 45) < 1.0
    assert stream._ramp(80, 45) == 1.0


def test_ramp_of_a_scenario_with_no_onset_is_always_zero():
    assert stream._ramp(50, -1) == 0.0


def test_refusal_creep_raises_the_refusal_rate():
    turns = stream.generate("refusal-creep")
    early = [t for t in turns if t.day < 20]
    late = [t for t in turns if t.day >= 80]
    rate = lambda ts: sum(1 for t in ts if t.is_refusal) / len(ts)
    assert rate(late) > rate(early) * 4


def test_other_scenarios_keep_a_flat_refusal_rate():
    """If refusals crept in everywhere, the refusal CUSUM would look like a general
    detector instead of the targeted one it is."""
    for key in ("healthy", "input-shift", "model-swap", "retrieval-decay"):
        turns = stream.generate(key)
        early = [t for t in turns if t.day < 20]
        late = [t for t in turns if t.day >= 80]
        rate = lambda ts: sum(1 for t in ts if t.is_refusal) / len(ts)
        assert abs(rate(late) - rate(early)) < 0.03, key


def test_template_regression_is_confined_to_one_topic():
    turns = stream.generate("template-regression")
    topics = {t.topic for t in turns if t.is_degraded}
    assert topics == {"warranty"}


def test_template_regression_affects_a_small_share_of_traffic():
    """The dilution that makes it survive: it must be small enough that aggregates miss it."""
    turns = stream.generate("template-regression")
    share = sum(1 for t in turns if t.is_degraded) / len(turns)
    assert 0.01 < share < 0.06


def test_topic_mixes_are_probability_distributions():
    for mix in (stream.BASE_TOPIC_MIX, stream.SHIFTED_TOPIC_MIX):
        assert abs(sum(mix.values()) - 1.0) < 1e-9
        assert all(v > 0 for v in mix.values())


def test_blend_interpolates_between_mixes():
    mid = stream._blend(stream.BASE_TOPIC_MIX, stream.SHIFTED_TOPIC_MIX, 0.5)
    assert abs(sum(mid.values()) - 1.0) < 1e-9
    for k in mid:
        lo, hi = sorted((stream.BASE_TOPIC_MIX[k], stream.SHIFTED_TOPIC_MIX[k]))
        assert lo <= mid[k] <= hi


def test_by_day_buckets_are_complete_and_disjoint():
    turns = stream.generate("healthy")
    buckets = stream.by_day(turns)
    assert len(buckets) == stream.DAYS
    assert sum(len(b) for b in buckets) == len(turns)
    assert all(all(t.day == d for t in b) for d, b in enumerate(buckets))


def test_unknown_scenario_key_raises():
    with pytest.raises(KeyError):
        stream.generate("no-such-scenario")


def test_quality_is_bounded():
    for scenario in stream.SCENARIOS:
        turns = stream.generate(scenario.key)
        assert all(0.0 <= t.quality <= 1.0 for t in turns), scenario.key


def test_degraded_turns_always_have_reduced_quality():
    for scenario in stream.SCENARIOS:
        for t in stream.generate(scenario.key):
            if t.is_degraded:
                assert t.quality < 1.0


def test_corpus_answers_differ_by_topic():
    import random

    rng = random.Random(0)
    billing = {corpus.answer("billing", rng) for _ in range(30)}
    warranty = {corpus.answer("warranty", rng) for _ in range(30)}
    assert not (billing & warranty)


def test_stale_answer_is_about_a_different_topic():
    """Retrieval decay's whole character: a fluent, confident answer to another question."""
    import random

    rng = random.Random(0)
    for _ in range(20):
        stale = corpus.stale_answer("billing", rng)
        assert stale not in {corpus.answer("billing", random.Random(s)) for s in range(5)}


def test_truncated_answer_is_shorter_than_a_real_one():
    import random

    rng = random.Random(0)
    full = corpus.answer("warranty", rng)
    cut = corpus.truncated_answer("warranty", rng)
    assert len(cut) < len(full)


def test_refusals_are_well_formed_prose():
    import random

    rng = random.Random(0)
    for _ in range(10):
        text = corpus.refusal(rng)
        assert len(text) > 20 and text.strip().endswith((".", "!"))


def test_scenario_keys_are_unique():
    keys = [s.key for s in stream.SCENARIOS]
    assert len(keys) == len(set(keys))


def test_exactly_one_healthy_control_and_one_non_regression_shift():
    non_regressions = [s.key for s in stream.SCENARIOS if not s.is_regression]
    assert sorted(non_regressions) == ["healthy", "input-shift"]
