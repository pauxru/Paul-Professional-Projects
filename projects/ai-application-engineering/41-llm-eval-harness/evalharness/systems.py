"""Deterministic stand-ins for a model under test and an LLM judge.

There is no LLM in this repository. That is a constraint of the build
environment, and it turned out to be the right design anyway.

The claims this project makes are about *statistical method*: that a 50-item
eval set cannot see a 5% regression, that sweeping twenty prompts and reporting
the winner is a 64% chance of reporting noise, that a judge with 92% agreement
can be worthless. Verifying any of those requires knowing the ground truth --
the real effect size, the real judge reliability -- and with a real LLM you
never know it. You would be measuring the method with an instrument whose
properties are exactly what the method is supposed to determine.

So the systems here are simulated with *known* parameters, and the harness is
then asked to recover them. When it says "this 3% improvement is not
distinguishable from noise", we can check whether the improvement was real,
because we set it. That is a stronger test of the harness than any live model
could provide.

The interfaces are the ones a real client would implement, so swapping in an
HTTP-backed model is a matter of one class.
"""

from __future__ import annotations

import hashlib
from dataclasses import dataclass
from typing import Protocol

import numpy as np

from .dataset import Dataset, Item


class Model(Protocol):
    """Anything that produces a response and its cost for an eval item."""

    name: str

    def run(self, item: Item) -> "Response":
        ...


@dataclass(frozen=True)
class Response:
    """A model output plus what it cost to get it."""

    item_id: str
    text: str
    quality: float
    """Ground-truth quality in [0, 1]. Only a simulation can know this."""

    latency_ms: float
    input_tokens: int
    output_tokens: int

    def cost_usd(self, input_per_1k: float, output_per_1k: float) -> float:
        return (self.input_tokens / 1000 * input_per_1k
                + self.output_tokens / 1000 * output_per_1k)


def _item_seed(item_id: str, salt: str) -> int:
    """A stable per-item seed.

    Derived from the item id rather than drawn from a shared stream so that
    adding an item to the dataset does not change the responses generated for
    every item after it. Without this, growing the eval set perturbs every
    score and makes historical comparisons meaningless -- which is a
    reproducibility bug that would be very hard to attribute.
    """
    h = hashlib.sha256(f"{salt}:{item_id}".encode("utf-8")).digest()
    return int.from_bytes(h[:8], "big")


@dataclass
class SimulatedModel:
    """A model with known quality characteristics.

    `base_quality` is the mean quality on a medium-tier item. `tier_offset`
    shifts it per tier, and `spread` is the item-to-item variability -- which
    is the parameter that decides how large an eval set has to be, and which
    nobody measures before choosing one.

    The offset is *signed*, and it is named that way after the alternative
    caused a bug. It was originally `tier_penalty`, holding values like
    `{"hard": -0.18}`, and applied as `base_quality + tier_penalty[tier]`.
    Every call site in this repository was correct, because the same person
    wrote the field and its callers and never had to read the name to know
    what it meant. The first caller written by someone reading the name --
    a test, some weeks later -- passed `{"hard": 0.25}` meaning "make hard
    items 0.25 worse" and got a model that was 0.25 *better* on them. The
    assertion failed, which is the only reason it was noticed; had the test
    been checking something less direct it would have passed with an inverted
    fixture underneath it.

    `shared_variance` is the subtle one, and getting it wrong makes the whole
    experiment a lie. Quality on an item is split into two parts: an effect
    that belongs to the *item* (this question is hard, and it is hard for every
    model) and an effect that belongs to the *model on that item*. The first is
    shared between any two models being compared; the second is not.

    That split is the intraclass correlation, and it is the entire reason
    paired analysis works. If two models' item-level performance were
    independent, pairing would buy nothing and the paired bootstrap would be
    pure ceremony. A simulation that generated independent models would
    therefore make paired statistics look worthless, and a simulation that
    generated perfectly correlated ones would make them look magical. Neither
    would tell you anything about your eval set. The number is a property of
    the *eval set*, not of the statistics, which is why it is a parameter here
    and estimated, not assumed, in the experiments.
    """

    name: str
    base_quality: float = 0.75
    spread: float = 0.18
    shared_variance: float = 0.60
    tier_offset: dict[str, float] | None = None
    latency_ms: float = 900.0
    latency_spread: float = 220.0
    tokens_in: int = 480
    tokens_out: int = 160
    seed: int = 0

    def __post_init__(self) -> None:
        if not 0.0 <= self.shared_variance <= 1.0:
            raise ValueError("shared_variance is a variance fraction in [0, 1]")
        if self.tier_offset is None:
            self.tier_offset = {"easy": 0.12, "medium": 0.0, "hard": -0.18,
                                 "adversarial": -0.34}

    def run(self, item: Item) -> Response:
        # The item effect is keyed on the item alone, so every model in a
        # comparison draws the same value for it.
        item_rng = np.random.default_rng(_item_seed(item.id, "item-difficulty"))
        own_rng = np.random.default_rng(_item_seed(item.id, f"{self.name}:{self.seed}"))

        rho = self.shared_variance
        shared = item_rng.normal(0.0, 1.0) * (rho ** 0.5)
        private = own_rng.normal(0.0, 1.0) * ((1.0 - rho) ** 0.5)

        mean = self.base_quality + self.tier_offset.get(item.tier, 0.0)
        quality = float(np.clip(mean + self.spread * (shared + private), 0.0, 1.0))
        latency = float(max(1.0, own_rng.normal(self.latency_ms, self.latency_spread)))
        return Response(
            item_id=item.id,
            text=f"[{self.name}] response to {item.id}",
            quality=quality,
            latency_ms=latency,
            input_tokens=self.tokens_in + len(item.prompt) // 4,
            output_tokens=int(max(1, own_rng.normal(self.tokens_out,
                                                    self.tokens_out * 0.25))),
        )

    def variant(self, name: str, *, quality_delta: float = 0.0, **kw) -> "SimulatedModel":
        """A copy with a known true effect applied.

        The whole experiment rests on being able to say "this candidate is
        exactly 0.03 better" and then asking whether the harness can tell.
        """
        return SimulatedModel(
            name=name,
            base_quality=self.base_quality + quality_delta,
            spread=kw.get("spread", self.spread),
            shared_variance=kw.get("shared_variance", self.shared_variance),
            tier_offset=dict(self.tier_offset),
            latency_ms=kw.get("latency_ms", self.latency_ms),
            latency_spread=kw.get("latency_spread", self.latency_spread),
            tokens_in=kw.get("tokens_in", self.tokens_in),
            tokens_out=kw.get("tokens_out", self.tokens_out),
            seed=kw.get("seed", self.seed),
        )


class Judge(Protocol):
    """Anything that scores a response."""

    name: str

    def score(self, item: Item, response: Response) -> float:
        ...


@dataclass
class SimulatedJudge:
    """A judge with known reliability, bias and failure modes.

    Four knobs, each corresponding to a way real judges go wrong:

    * `noise` -- total scoring error magnitude.
    * `consistency` -- the fraction of that error which is a *stable per-item*
      offset rather than fresh randomness. This distinction is the one that
      matters and the one that is almost never made. A judge that always reads
      item 17 as 0.1 too generous is scoring both models 0.1 too generously,
      so the offset cancels exactly in a paired difference and costs nothing.
      A judge that is fresh-random costs you directly in variance. Two judges
      with identical raw agreement against humans can therefore differ by a
      large factor in how many eval items you need, and no agreement statistic
      will tell you which one you have.

      Getting this wrong is not hypothetical: the first version of this class
      keyed judge noise on the item id alone, which made *all* judge error
      stable and therefore free. Judge quality appeared not to matter at all,
      which was a pleasant and completely false result.
    * `bias` -- a constant offset. Systematically generous or harsh. Cancels in
      an A/B difference and ruins every absolute score, which is why ranking
      ability and calibration are reported separately.
    * `length_bias` -- scoring longer answers higher regardless of content.
      This is the documented, reproducible failure of real LLM judges, and it
      is the dangerous one, because it correlates with the thing under test: a
      prompt change that makes the model more verbose will look like a quality
      improvement, and it will look like one consistently, so more eval items
      and tighter intervals make you *more* confident of something false.
    """

    name: str
    noise: float = 0.08
    consistency: float = 0.5
    bias: float = 0.0
    length_bias: float = 0.0
    seed: int = 0

    def __post_init__(self) -> None:
        if not 0.0 <= self.consistency <= 1.0:
            raise ValueError("consistency is a variance fraction in [0, 1]")

    def score(self, item: Item, response: Response) -> float:
        stable_rng = np.random.default_rng(
            _item_seed(response.item_id, f"judge-item:{self.name}:{self.seed}"))
        fresh_rng = np.random.default_rng(
            _item_seed(f"{response.item_id}|{response.text}",
                       f"judge-fresh:{self.name}:{self.seed}"))

        c = self.consistency
        stable = stable_rng.normal(0.0, 1.0) * (c ** 0.5)
        fresh = fresh_rng.normal(0.0, 1.0) * ((1.0 - c) ** 0.5)

        length_effect = self.length_bias * (response.output_tokens - 160) / 160
        raw = (response.quality + self.bias + length_effect
               + self.noise * (stable + fresh))
        return float(np.clip(raw, 0.0, 1.0))


@dataclass(frozen=True)
class RunResult:
    """One model evaluated once over one dataset version."""

    model: str
    judge: str
    dataset_name: str
    dataset_version: str
    dataset_fingerprint: str
    item_ids: tuple[str, ...]
    scores: np.ndarray
    truth: np.ndarray
    latencies_ms: np.ndarray
    input_tokens: np.ndarray
    output_tokens: np.ndarray

    @property
    def mean_score(self) -> float:
        return float(self.scores.mean())

    @property
    def mean_truth(self) -> float:
        return float(self.truth.mean())

    @property
    def p50_latency(self) -> float:
        return float(np.quantile(self.latencies_ms, 0.50))

    @property
    def p95_latency(self) -> float:
        """The latency number worth gating on.

        A mean latency hides the tail, and the tail is what users experience as
        "it's broken". A change that improves the mean while doubling P95 is a
        regression that a mean-only report calls an improvement.
        """
        return float(np.quantile(self.latencies_ms, 0.95))

    def cost_usd(self, input_per_1k: float = 0.003, output_per_1k: float = 0.015) -> float:
        return float(self.input_tokens.sum() / 1000 * input_per_1k
                     + self.output_tokens.sum() / 1000 * output_per_1k)

    def slice_scores(self, dataset: Dataset, tier: str) -> np.ndarray:
        wanted = {i.id for i in dataset.by_tier(tier)}
        idx = [k for k, iid in enumerate(self.item_ids) if iid in wanted]
        return self.scores[idx]


def run(model: SimulatedModel, judge: SimulatedJudge, dataset: Dataset) -> RunResult:
    """Evaluate a model over a dataset with a judge.

    Item order is the dataset's order and is preserved in every returned array,
    because every paired statistic downstream depends on index i meaning the
    same item in both runs. That invariant is checked in `compare.py` rather
    than assumed.
    """
    responses = [model.run(item) for item in dataset.items]
    scores = np.array([judge.score(item, r) for item, r in zip(dataset.items, responses)])
    return RunResult(
        model=model.name,
        judge=judge.name,
        dataset_name=dataset.name,
        dataset_version=dataset.version,
        dataset_fingerprint=dataset.fingerprint,
        item_ids=dataset.ids(),
        scores=scores,
        truth=np.array([r.quality for r in responses]),
        latencies_ms=np.array([r.latency_ms for r in responses]),
        input_tokens=np.array([r.input_tokens for r in responses]),
        output_tokens=np.array([r.output_tokens for r in responses]),
    )
