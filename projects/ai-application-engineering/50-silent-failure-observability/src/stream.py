"""The traffic simulator, and the five ways it degrades.

Ninety days of a support assistant. Every request returns 200 and every latency is
normal, because that is the premise: these failures do not throw.

Each degradation is defined by the day it starts and what it does to the *text*. None of
them touch the embeddings, the status codes or the latency directly -- if a detector sees
something, it is because the words changed.
"""

from __future__ import annotations

import random
from dataclasses import dataclass
from typing import Callable

from . import corpus
from .corpus import Turn

DAYS = 90
REQUESTS_PER_DAY = 120
BASE_REFUSAL_RATE = 0.02

#: Topic mix at the start of the window. Sums to 1.
BASE_TOPIC_MIX: dict[str, float] = {
    "billing": 0.24,
    "shipping": 0.26,
    "returns": 0.20,
    "account": 0.12,
    "technical": 0.10,
    "warranty": 0.08,
}

#: Topic mix after the input-population shift. The users changed; the model did not.
SHIFTED_TOPIC_MIX: dict[str, float] = {
    "billing": 0.10,
    "shipping": 0.12,
    "returns": 0.14,
    "account": 0.16,
    "technical": 0.34,
    "warranty": 0.14,
}


@dataclass(frozen=True)
class Scenario:
    key: str
    title: str
    #: The day the degradation begins. Detectors are scored on how long after this they fire.
    onset_day: int
    #: What an operator would eventually be told, in one line.
    symptom: str
    #: Whether this is a genuine quality regression. `input-shift` is not.
    is_regression: bool
    story: str


SCENARIOS: tuple[Scenario, ...] = (
    Scenario(
        key="model-swap",
        title="Silent model swap",
        onset_day=45,
        symptom="answers become fluent, shorter and free of specifics",
        is_regression=True,
        story=(
            "The provider routes a share of traffic to a cheaper or more heavily quantised "
            "variant. Nothing in the API contract changes. The answers stay grammatical and "
            "polite and stop containing the order number, the amount or the action taken."
        ),
    ),
    Scenario(
        key="retrieval-decay",
        title="Retrieval decay",
        onset_day=45,
        symptom="answers are fluent, confident and about the wrong thing",
        is_regression=True,
        story=(
            "The index stops being rebuilt. Retrieval still returns k documents with "
            "plausible scores, they are simply the wrong documents. The model writes an "
            "excellent answer to a question nobody asked."
        ),
    ),
    Scenario(
        key="input-shift",
        title="Input population shift",
        onset_day=45,
        symptom="nothing is wrong; the questions changed",
        is_regression=False,
        story=(
            "A marketing push brings in a different cohort and the topic mix moves towards "
            "technical support. The model is performing exactly as well as it was. Any "
            "detector that alerts here is producing a false positive, and this scenario is "
            "in the panel specifically to catch detectors that cannot tell the difference."
        ),
    ),
    Scenario(
        key="refusal-creep",
        title="Refusal creep",
        onset_day=45,
        symptom="the assistant declines a growing share of answerable questions",
        is_regression=True,
        story=(
            "A safety filter is tuned upstream. Refusals climb from 2% to 18% over a "
            "fortnight. Every refusal is a 200 with a polite, well formed body, and the "
            "user is simply not helped."
        ),
    ),
    Scenario(
        key="template-regression",
        title="Prompt template regression",
        onset_day=45,
        symptom="8% of traffic gets a truncated answer",
        is_regression=True,
        story=(
            "A deploy drops a field from the prompt template for one topic. Ninety two "
            "percent of traffic is unaffected, which is what makes it survive: every "
            "aggregate is diluted by a factor of twelve and the golden set never touches "
            "the affected topic."
        ),
    ),
    Scenario(
        key="healthy",
        title="No degradation (control)",
        onset_day=-1,
        symptom="nothing happens for ninety days",
        is_regression=False,
        story=(
            "The control. Every alert raised here is a false positive, and a detector's "
            "false alarm count on this stream is the price of its sensitivity elsewhere."
        ),
    ),
)

SCENARIOS_BY_KEY: dict[str, Scenario] = {s.key: s for s in SCENARIOS}


def _ramp(day: int, onset: int, days_to_full: int = 14) -> float:
    """Degradations arrive gradually. A step change is easy to detect and rare in life."""
    if onset < 0 or day < onset:
        return 0.0
    return min(1.0, (day - onset + 1) / days_to_full)


def _pick_topic(mix: dict[str, float], rng: random.Random) -> str:
    roll = rng.random()
    cumulative = 0.0
    for topic, weight in mix.items():
        cumulative += weight
        if roll < cumulative:
            return topic
    return corpus.TOPICS[-1]


def _blend(a: dict[str, float], b: dict[str, float], t: float) -> dict[str, float]:
    return {k: a[k] * (1 - t) + b[k] * t for k in a}


def generate(scenario_key: str, seed: int = 20260904, days: int = DAYS) -> list[Turn]:
    """Generate one scenario's worth of traffic."""
    scenario = SCENARIOS_BY_KEY[scenario_key]
    rng = random.Random(seed)
    turns: list[Turn] = []

    for day in range(days):
        severity = _ramp(day, scenario.onset_day)

        mix = BASE_TOPIC_MIX
        if scenario.key == "input-shift":
            mix = _blend(BASE_TOPIC_MIX, SHIFTED_TOPIC_MIX, severity)

        for _ in range(REQUESTS_PER_DAY):
            topic = _pick_topic(mix, rng)
            q = corpus.question(topic, rng)

            answer_text = corpus.answer(topic, rng)
            degraded = False
            refused = False
            quality = 1.0

            if scenario.key == "model-swap" and rng.random() < severity * 0.6:
                answer_text = corpus.generic_answer(rng)
                degraded, quality = True, 0.35

            elif scenario.key == "retrieval-decay" and rng.random() < severity * 0.5:
                answer_text = corpus.stale_answer(topic, rng)
                degraded, quality = True, 0.15

            elif scenario.key == "template-regression" and topic == "warranty":
                # Only one topic is affected, and it is 8% of traffic.
                if rng.random() < severity:
                    answer_text = corpus.truncated_answer(topic, rng)
                    # A truncated answer is not a worse answer, it is not an answer. An
                    # earlier version scored it 0.4, which put full-severity mean quality
                    # at 0.95 -- exactly the materiality threshold -- so the ground-truth
                    # onset day was decided by which days happened to be noisy.
                    degraded, quality = True, 0.1

            if scenario.key == "refusal-creep":
                rate = BASE_REFUSAL_RATE + severity * 0.16
            else:
                rate = BASE_REFUSAL_RATE
            if not degraded and rng.random() < rate:
                answer_text = corpus.refusal(rng)
                refused = True
                # Only the excess over the baseline rate is a degradation, and only in the
                # scenario that causes it. An earlier version applied this to every
                # scenario, which quietly gave `input-shift` -- the scenario whose whole
                # job is to be a non-regression -- a ground-truth quality drop, and turned
                # the control for false positives into another positive.
                if scenario.key == "refusal-creep" and rng.random() < severity:
                    degraded, quality = True, 0.0

            turns.append(
                Turn(
                    day=day,
                    topic=topic,
                    question=q,
                    answer=answer_text,
                    # Latency is deliberately independent of everything above. A cheaper
                    # model is usually *faster*, which is why latency alerts do not fire.
                    latency_ms=max(80.0, rng.gauss(420.0, 90.0)),
                    http_status=200,
                    is_degraded=degraded,
                    is_refusal=refused,
                    quality=quality,
                )
            )

    return turns


def by_day(turns: list[Turn], days: int = DAYS) -> list[list[Turn]]:
    buckets: list[list[Turn]] = [[] for _ in range(days)]
    for t in turns:
        buckets[t.day].append(t)
    return buckets


def true_quality_series(turns: list[Turn], days: int = DAYS) -> list[float]:
    """Ground truth mean quality per day. No detector may look at this."""
    out: list[float] = []
    for bucket in by_day(turns, days):
        out.append(sum(t.quality for t in bucket) / len(bucket) if bucket else 1.0)
    return out


def first_materially_degraded_day(
    turns: list[Turn], threshold: float = 0.95, persistence: int = 3
) -> int:
    """The first day of the first run of `persistence` days below `threshold` quality.

    Detection delay is measured from here rather than from the onset day, because on the
    onset day the ramp has barely started and no honest detector could fire. Measuring
    from onset would flatter nothing and penalise everything equally, but it would also
    make the numbers meaningless.

    The persistence requirement is the same one every detector pays. Without it a single
    noisy day decides the ground truth, and every detection delay in the panel is then
    measured against a coin flip -- which is how the first version of this function
    reported that a localised regression became material on day 52 and, on a different
    seed, day 61.
    """
    run = 0
    for day, q in enumerate(true_quality_series(turns)):
        if q < threshold:
            run += 1
            if run >= persistence:
                return day - persistence + 1
        else:
            run = 0
    return -1
