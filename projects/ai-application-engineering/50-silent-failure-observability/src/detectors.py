"""The detector panel.

Every detector has the same shape and the same contract:

* it sees a reference window (the first `REFERENCE_DAYS` days, assumed healthy) and then
  one day at a time;
* it may look at questions, answers, latency and status codes;
* it may **not** look at `Turn.quality` or `Turn.is_degraded`. Those are ground truth and
  exist only for scoring.

Every threshold is calibrated the same way -- the `1 - ALPHA` quantile of the detector's
own scores over the reference window -- so the comparison between detectors is a
comparison of *signal*, not of who picked a luckier constant. A hardcoded threshold is a
hyperparameter pretending to be a statistic, and it is how monitoring comparisons are
usually rigged without anyone intending to.
"""

from __future__ import annotations

import random as _random
from dataclasses import dataclass, field
from typing import Callable, Sequence

import numpy as np

from . import embedding
from .corpus import Turn

REFERENCE_DAYS = 30
#: Consecutive days over threshold before an alert counts. One day over threshold is a
#: blip; three is a trend. Every detector pays the same price.
PERSISTENCE = 3
#: Target per-day false positive rate on healthy traffic.
ALPHA = 0.01
#: Minimum samples in a slice before it is scored. Below this PSI is reporting on sample
#: size rather than on drift.
MIN_SLICE = 8
#: Fewer bins for sliced PSI: ten quantile bins over eight samples is not a histogram.
SLICE_BINS = 4


@dataclass
class DetectorResult:
    name: str
    scores: list[float]
    threshold: float
    #: Per-day cost in units of "one model call". Only the canary is non-zero.
    calls_per_day: int = 0
    notes: str = ""


# ---------------------------------------------------------------------------
# statistics


class PSIReference:
    """Precomputed reference bins for PSI.

    The reference window does not change from day to day, but the first version of this
    code recomputed `np.quantile` over 256 dimensions for every day of every scenario --
    about eight hundred thousand redundant quantile calls once the sliced detector was
    added, and the dominant cost of the whole panel. The statistic is identical; only the
    arithmetic is done once.
    """

    __slots__ = ("_edges", "_ref_p", "_dims", "_kept")

    def __init__(self, reference: np.ndarray, bins: int = 10) -> None:
        self._edges: list[np.ndarray] = []
        self._ref_p: list[np.ndarray] = []
        #: Which source dimensions survived the degenerate-bin filter. Tracked explicitly
        #: because a positional counter silently misaligns the moment one is dropped.
        self._kept: list[int] = []
        self._dims = 0
        if reference.size == 0:
            return

        for d in range(reference.shape[1]):
            # A dimension that is constant across the reference window carries no
            # information and cannot support a quantile binning: every edge collapses to
            # the same value, and the two infinite outer edges then map the whole real
            # line onto a single bin -- so a *genuine* shift in that dimension scores
            # exactly zero drift. Dropping it is both cheaper and more honest than
            # scoring it.
            #
            # The original guard here tested `len(np.unique(edges)) < 3`, which cannot
            # fire for any finite input, because the two infinite edges always survive the
            # unique. It was unreachable code protecting against a case it could not
            # detect, and the mutation harness found it by reporting a mutant that no test
            # could possibly kill.
            column = reference[:, d]
            if float(np.ptp(column)) == 0.0 or not np.all(np.isfinite(column)):
                continue
            edges = np.quantile(column, np.linspace(0.0, 1.0, bins + 1))
            edges[0], edges[-1] = -np.inf, np.inf
            edges = np.unique(edges)
            if len(edges) < 3:
                continue
            counts = _bin_counts(column, edges)
            self._edges.append(edges)
            self._ref_p.append(_probabilities(counts))
            self._kept.append(d)
            self._dims += 1

    def score(self, current: np.ndarray) -> float:
        if current.size == 0 or self._dims == 0:
            return 0.0
        total = 0.0
        for slot, d in enumerate(self._kept):
            if d >= current.shape[1]:
                continue
            edges = self._edges[slot]
            ref_p = self._ref_p[slot]
            cur_p = _probabilities(_bin_counts(current[:, d], edges))
            total += float(np.sum((cur_p - ref_p) * np.log(cur_p / ref_p)))
        return total / max(1, self._dims)


def _bin_counts(values: np.ndarray, edges: np.ndarray) -> np.ndarray:
    idx = np.searchsorted(edges[1:-1], values, side="right")
    return np.bincount(idx, minlength=len(edges) - 1)


def _probabilities(counts: np.ndarray) -> np.ndarray:
    p = counts / max(1, counts.sum())
    # Floor the probabilities. Without it an empty bin sends one term to infinity and the
    # statistic becomes a report about sample size.
    return np.clip(p, 1e-6, None)


def population_stability_index(
    reference: np.ndarray, current: np.ndarray, bins: int = 10
) -> float:
    """PSI per dimension, averaged.

    This is how PSI is actually applied to embeddings in production monitoring: treat
    each dimension as a feature, bin it against the reference, aggregate. The consequence
    is worth stating plainly -- it is a sum of *marginal* comparisons and is blind to any
    change in the relationship between dimensions. A rotation of the semantic cloud that
    leaves every margin intact is invisible to it, and MMD exists in this panel to have
    something that is not.

    Kept as the one-shot form for tests and for callers scoring a single pair; the panel
    itself builds a `PSIReference` once and reuses it.
    """
    if reference.size == 0 or current.size == 0:
        return 0.0
    return PSIReference(reference, bins=bins).score(current)


def _rbf(a: np.ndarray, b: np.ndarray, gamma: float) -> np.ndarray:
    sq = np.sum(a * a, axis=1)[:, None] + np.sum(b * b, axis=1)[None, :] - 2.0 * (a @ b.T)
    return np.exp(-gamma * np.maximum(sq, 0.0))


def median_heuristic_gamma(sample: np.ndarray, rng: np.random.Generator) -> float:
    """gamma = 1 / median squared pairwise distance -- the standard bandwidth choice."""
    n = min(200, sample.shape[0])
    if n < 2:
        return 1.0
    sub = sample[rng.choice(sample.shape[0], size=n, replace=False)]
    sq = np.sum(sub * sub, axis=1)[:, None] + np.sum(sub * sub, axis=1)[None, :] - 2.0 * (sub @ sub.T)
    median = float(np.median(sq[np.triu_indices(n, k=1)]))
    return 1.0 / median if median > 0 else 1.0


def mmd_squared(x: np.ndarray, y: np.ndarray, gamma: float) -> float:
    """Unbiased squared maximum mean discrepancy with an RBF kernel.

    A genuine two-sample test on the joint distribution, so unlike PSI it can see a change
    in the shape of the cloud and not only in its margins.
    """
    n, m = x.shape[0], y.shape[0]
    if n < 2 or m < 2:
        return 0.0
    kxx = _rbf(x, x, gamma)
    kyy = _rbf(y, y, gamma)
    kxy = _rbf(x, y, gamma)
    np.fill_diagonal(kxx, 0.0)
    np.fill_diagonal(kyy, 0.0)
    return float(kxx.sum() / (n * (n - 1)) + kyy.sum() / (m * (m - 1)) - 2.0 * kxy.mean())


def cusum(series: Sequence[float], target: float, slack: float, reset_at: int = -1) -> list[float]:
    """One-sided upper CUSUM.

    Chosen over a rolling mean because a slow ramp is the shape a rolling mean handles
    worst: by the time the window has filled, the window itself has moved with the drift
    and the comparison is against the degraded state.

    `reset_at` zeroes the accumulator at that index. The reference window is used to
    estimate `target` and `slack`; monitoring then starts from a clean sheet. Without the
    reset the live statistic arrives at day `REFERENCE_DAYS` carrying whatever the healthy
    window happened to accumulate, while any threshold calibrated by simulating a run of
    the monitoring horizon starts at zero -- and the two are then not measuring the same
    quantity, which is a slow way to buy false alarms.
    """
    out: list[float] = []
    s = 0.0
    for i, x in enumerate(series):
        if i == reset_at:
            s = 0.0
        s = max(0.0, s + (x - target - slack))
        out.append(s)
    return out


def calibrate(scores: Sequence[float], floor: float, alpha: float = ALPHA) -> float:
    """Threshold = the (1 - alpha) quantile of the detector's own reference-window scores.

    The floor exists because a detector whose reference scores are all identically zero
    would otherwise get a threshold of zero and alert on the first floating-point wobble.
    """
    window = [s for s in scores[:REFERENCE_DAYS]]
    if not window:
        return floor
    return max(floor, float(np.quantile(window, 1.0 - alpha)))


def bootstrap_cusum_threshold(
    reference_rates: Sequence[float], horizon: int, seed: int = 3,
    trials: int = 400, alpha: float = ALPHA,
) -> float:
    """Calibrate a CUSUM threshold by simulating the whole monitoring procedure.

    A CUSUM is a reflected random walk: on pure noise it wanders upward and eventually
    crosses *any* fixed threshold, so calibrating it against the quantile of its own
    reference-window values guarantees a false alarm. The question is not "how large does
    it get on healthy data" but "how large does it get over a horizon as long as the one I
    intend to run it for", which is what the classical average-run-length calibration asks.

    That is necessary and it is not sufficient, which cost this panel two false-alarm
    scenarios before the second half of the problem became visible. The live detector
    estimates its target from a *finite* reference window and then applies it to *fresh*
    days. If the sample mean of those thirty days lands one standard error below the true
    rate -- a coin flip -- then every subsequent day is, on average, slightly above target,
    and a one-sided CUSUM integrates that estimation error into a linear ramp
    indistinguishable from a real one. Self-starting CUSUMs exist for this reason.

    So each trial resamples the reference window *and* the horizon separately: target and
    slack come from one bootstrap sample, the monitored days come from another. The
    threshold that results is larger, and it is larger by exactly the amount the estimation
    error is worth.
    """
    rng = np.random.default_rng(seed)
    rates = np.asarray(reference_rates, dtype=np.float64)
    if rates.size == 0:
        return float("inf")
    maxima = []
    for _ in range(trials):
        ref = rng.choice(rates, size=rates.size, replace=True)
        target = float(ref.mean())
        slack = float(ref.std())
        sample = rng.choice(rates, size=horizon, replace=True)
        maxima.append(max(cusum(sample.tolist(), target=target, slack=slack)))
    return float(np.quantile(maxima, 1.0 - alpha))


def first_alert(
    scores: Sequence[float], threshold: float, persistence: int = PERSISTENCE,
    start_day: int = REFERENCE_DAYS,
) -> int:
    """The first day of the first run of `persistence` consecutive days over threshold."""
    run = 0
    for day in range(start_day, len(scores)):
        if scores[day] > threshold:
            run += 1
            if run >= persistence:
                return day - persistence + 1
        else:
            run = 0
    return -1


def alert_days(
    scores: Sequence[float], threshold: float, persistence: int = PERSISTENCE,
    start_day: int = REFERENCE_DAYS,
) -> list[int]:
    """Every day on which the detector is in an alerting state."""
    days: list[int] = []
    run = 0
    for day in range(start_day, len(scores)):
        if scores[day] > threshold:
            run += 1
            if run >= persistence:
                days.append(day)
        else:
            run = 0
    return days


# ---------------------------------------------------------------------------
# detectors


def _buckets(turns: list[Turn], days: int) -> list[list[Turn]]:
    out: list[list[Turn]] = [[] for _ in range(days)]
    for t in turns:
        out[t.day].append(t)
    return out


def apm_baseline(turns: list[Turn], days: int) -> DetectorResult:
    """Error rate and p95 latency: what an APM tool watches. The control."""
    scores = []
    for b in _buckets(turns, days):
        if not b:
            scores.append(0.0)
            continue
        errors = sum(1 for t in b if t.http_status >= 400) / len(b)
        p95 = float(np.quantile([t.latency_ms for t in b], 0.95))
        scores.append(max(errors * 100.0, (p95 - 600.0) / 600.0))
    return DetectorResult(
        name="APM (error rate + p95 latency)",
        scores=scores,
        threshold=calibrate(scores, floor=0.05),
        notes="the control: every request is a 200 and a cheaper model is not slower",
    )


def _embedding_psi(turns: list[Turn], days: int, field_name: str, name: str, notes: str) -> DetectorResult:
    buckets = _buckets(turns, days)
    reference = PSIReference(
        embedding.embed_all(
            [getattr(t, field_name) for b in buckets[:REFERENCE_DAYS] for t in b]
        )
    )
    scores = []
    for b in buckets:
        if not b:
            scores.append(0.0)
            continue
        current = embedding.embed_all([getattr(t, field_name) for t in b])
        scores.append(reference.score(current))
    return DetectorResult(
        name=name, scores=scores, threshold=calibrate(scores, floor=1e-4), notes=notes
    )


def output_psi(turns: list[Turn], days: int) -> DetectorResult:
    return _embedding_psi(
        turns, days, "answer", "PSI on output embeddings",
        "per-dimension marginal drift, the tabular-monitoring default applied to text",
    )


def input_psi(turns: list[Turn], days: int) -> DetectorResult:
    return _embedding_psi(
        turns, days, "question", "PSI on input embeddings",
        "watches what users ask; cannot tell a new cohort from a regression",
    )


def output_mmd(turns: list[Turn], days: int, seed: int = 7) -> DetectorResult:
    buckets = _buckets(turns, days)
    rng = np.random.default_rng(seed)
    reference = embedding.embed_all(
        [t.answer for b in buckets[:REFERENCE_DAYS] for t in b]
    )
    gamma = median_heuristic_gamma(reference, rng)
    sample = min(120, max(2, reference.shape[0] // 2))

    scores = []
    for b in buckets:
        if not b:
            scores.append(0.0)
            continue
        current = embedding.embed_all([t.answer for t in b])
        idx = rng.choice(reference.shape[0], size=sample, replace=False)
        scores.append(mmd_squared(reference[idx], current, gamma))

    return DetectorResult(
        name="MMD on output embeddings",
        scores=scores,
        threshold=calibrate(scores, floor=1e-9),
        notes="kernel two-sample test on the joint distribution, not the margins",
    )


def refusal_rate_cusum(turns: list[Turn], days: int) -> DetectorResult:
    buckets = _buckets(turns, days)
    rates = [(sum(1 for t in b if t.is_refusal) / len(b)) if b else 0.0 for b in buckets]
    baseline = float(np.mean(rates[:REFERENCE_DAYS]))
    noise = float(np.std(rates[:REFERENCE_DAYS]))
    scores = cusum(rates, target=baseline, slack=noise, reset_at=REFERENCE_DAYS)
    return DetectorResult(
        name="Refusal-rate CUSUM",
        scores=scores,
        threshold=bootstrap_cusum_threshold(rates[:REFERENCE_DAYS], horizon=days - REFERENCE_DAYS),
        notes="a refusal is a 200 with a polite body; nothing else in the panel looks for one",
    )


def answer_diversity(turns: list[Turn], days: int) -> DetectorResult:
    """Absolute deviation of the day's mean pairwise answer similarity from baseline.

    This detector was written to catch a model collapsing into boilerplate, on the
    reasoning that boilerplate answers resemble *each other*. Measured, the effect ran the
    other way: when 60% of traffic switched to a generic template pool the mean pairwise
    similarity **fell** by 0.04, because a day containing two tight clusters is less
    self-similar than a day containing one. The directional hypothesis was wrong, and the
    honest repair is to drop it -- the statistic is scored two-sided, on the deviation
    from the healthy baseline in either direction, which is all the reference window
    actually licenses.

    One number, no model calls, and it still says nothing at all about correctness.
    """
    buckets = _buckets(turns, days)
    similarity = []
    for b in buckets:
        if not b:
            similarity.append(0.0)
            continue
        similarity.append(embedding.mean_pairwise_cosine(embedding.embed_all([t.answer for t in b])))
    baseline = float(np.mean(similarity[:REFERENCE_DAYS]))
    scores = [abs(s - baseline) for s in similarity]
    return DetectorResult(
        name="Answer self-similarity",
        scores=scores,
        threshold=calibrate(scores, floor=1e-4),
        notes="two-sided: partial contamination moves this down, not up",
    )


def sliced_output_psi(turns: list[Turn], days: int) -> DetectorResult:
    """Output PSI computed *within each topic*, scored on the worst topic.

    Two things follow from conditioning on the topic, and they pull in opposite directions
    from what a monitoring team usually expects.

    A regression confined to one cohort is no longer diluted. The aggregate detectors see
    a truncated-answer bug in an 8%-of-traffic topic as an 8% contamination and a twelvefold
    reduction in effect size; sliced, it is a 100% contamination of one slice.

    A shift in the *mix* of cohorts stops registering at all. Conditioning on the topic
    removes exactly the variable that `input-shift` moves, so the detector that is most
    sensitive to localised regressions is also the one immune to the panel's designated
    false positive. Sensitivity and specificity are not always traded against each other;
    sometimes they are both bought by asking a better-posed question.

    The price is paid in sample size -- the smallest topic is ten requests a day, and PSI
    on ten samples is loud. Calibrating the threshold on the *max over topics* over the
    reference window pays for that and for the multiple comparison at the same time, since
    the reference maxima are drawn from the same six-way max the live score is.
    """
    buckets = _buckets(turns, days)
    topics = sorted({t.topic for t in turns})

    reference: dict[str, PSIReference] = {}
    sizes: dict[str, int] = {}
    for topic in topics:
        matrix = embedding.embed_all(
            [t.answer for b in buckets[:REFERENCE_DAYS] for t in b if t.topic == topic]
        )
        sizes[topic] = matrix.shape[0]
        reference[topic] = PSIReference(matrix, bins=SLICE_BINS)

    scores: list[float] = []
    for b in buckets:
        worst = 0.0
        for topic in topics:
            todays = [t.answer for t in b if t.topic == topic]
            if len(todays) < MIN_SLICE or sizes[topic] < MIN_SLICE:
                continue
            worst = max(worst, reference[topic].score(embedding.embed_all(todays)))
        scores.append(worst)

    return DetectorResult(
        name="Sliced PSI (worst topic)",
        scores=scores,
        threshold=calibrate(scores, floor=1e-4),
        notes="conditions on topic: undiluted by cohort size, immune to mix shift",
    )


GOLDEN_TOPICS: tuple[str, ...] = ("billing", "shipping", "returns", "account")
GOLDEN_SET_SIZE = 24


def quality_canary(turns: list[Turn], days: int, seed: int = 11) -> DetectorResult:
    """A fixed golden set replayed daily, scored against the reference answer centroid.

    The strongest signal in the panel and the only one that costs money. It is also the
    only detector whose blind spot is a *decision*: the golden set was written at launch
    and covers the four topics that mattered then.
    """
    rng = _random.Random(seed)
    buckets = _buckets(turns, days)

    centroid = {}
    for topic in GOLDEN_TOPICS:
        matrix = embedding.embed_all(
            [t.answer for b in buckets[:REFERENCE_DAYS] for t in b
             if t.topic == topic and not t.is_refusal]
        )
        centroid[topic] = matrix.mean(axis=0) if matrix.shape[0] else np.zeros(embedding.DIMENSIONS)

    per_topic = max(1, GOLDEN_SET_SIZE // len(GOLDEN_TOPICS))
    raw = []
    for b in buckets:
        sims = []
        for topic in GOLDEN_TOPICS:
            todays = [t for t in b if t.topic == topic]
            if not todays:
                continue
            for t in rng.sample(todays, min(per_topic, len(todays))):
                sims.append(embedding.cosine(embedding.embed(t.answer), centroid[topic]))
        raw.append(float(np.mean(sims)) if sims else 0.0)

    baseline = float(np.mean(raw[:REFERENCE_DAYS]))
    # Inverted so that, like every other detector in the panel, higher is worse.
    scores = [baseline - s for s in raw]
    return DetectorResult(
        name="Quality canary (golden set)",
        scores=scores,
        threshold=calibrate(scores, floor=1e-4),
        calls_per_day=GOLDEN_SET_SIZE,
        notes=f"{GOLDEN_SET_SIZE} calls/day; covers {len(GOLDEN_TOPICS)} of 6 topics",
    )


DETECTORS: tuple[Callable[[list[Turn], int], DetectorResult], ...] = (
    apm_baseline,
    output_psi,
    input_psi,
    output_mmd,
    refusal_rate_cusum,
    answer_diversity,
    sliced_output_psi,
    quality_canary,
)
