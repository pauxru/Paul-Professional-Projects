"""Decoding strategies, and the cost model that decides between them.

Three strategies are implemented because the interesting question is not "does
constrained decoding work" but "when is it worth it":

  * `decode_constrained`  -- mask every step. Always valid, one pass.
  * `decode_retry`        -- sample freely, validate, resample on failure.
                             Always valid *eventually*, unbounded cost.
  * `decode_unconstrained` -- the baseline, used to measure the base error rate.
"""

from __future__ import annotations

from dataclasses import dataclass

from ._ffi import DEAD, Engine, is_valid_json
from .model import BOS, BigramModel, SplitMix64, sample


@dataclass(frozen=True)
class Decoded:
    tokens: tuple[int, ...]
    text: bytes
    attempts: int
    tokens_generated: int
    valid: bool
    truncated: bool = False


def decode_constrained(engine: Engine, model: BigramModel, rng: SplitMix64,
                       max_tokens: int = 256) -> Decoded:
    """Sample under the automaton's mask. Cannot produce an invalid document.

    Note what this is *not*: it is not sampling from the model conditioned on
    validity. See `distortion.py` for the size of the gap.
    """
    state = engine.start_state
    prev = BOS
    tokens: list[int] = []
    for _ in range(max_tokens):
        allowed = engine.allowed_token_ids(state)
        if engine.eos_allowed(state):
            candidates = allowed + [model.eos_id]
        else:
            candidates = allowed
        if not candidates:
            # Unreachable for a trimmed automaton, but a decoder that can wedge
            # silently is worse than one that says so.
            raise RuntimeError(f"no legal continuation from state {state}")
        choice = sample(model, prev, rng, candidates)
        if choice == model.eos_id:
            text = b"".join(engine.tokens[t] for t in tokens)
            return Decoded(tuple(tokens), text, 1, len(tokens), True)
        nxt = engine.advance_token(state, choice)
        if nxt == DEAD:
            raise AssertionError("mask permitted a token that kills the automaton")
        state, prev = nxt, choice
        tokens.append(choice)
    text = b"".join(engine.tokens[t] for t in tokens)
    return Decoded(tuple(tokens), text, 1, len(tokens), False, truncated=True)


def decode_unconstrained(engine: Engine, model: BigramModel, rng: SplitMix64,
                         max_tokens: int = 256) -> Decoded:
    """Free sampling, validated afterwards. One attempt only."""
    prev = BOS
    tokens: list[int] = []
    for _ in range(max_tokens):
        choice = sample(model, prev, rng)
        if choice == model.eos_id:
            break
        tokens.append(choice)
        prev = choice
    else:
        text = b"".join(engine.tokens[t] for t in tokens)
        return Decoded(tuple(tokens), text, 1, len(tokens), False, truncated=True)
    text = b"".join(engine.tokens[t] for t in tokens)
    return Decoded(tuple(tokens), text, 1, len(tokens), engine.matches(text))


def decode_retry(engine: Engine, model: BigramModel, rng: SplitMix64,
                 max_attempts: int = 50, max_tokens: int = 256) -> Decoded:
    """The strategy most teams ship first: generate, validate, try again.

    Counts *total* tokens across attempts, because that is what is paid for.
    """
    total = 0
    for attempt in range(1, max_attempts + 1):
        result = decode_unconstrained(engine, model, rng, max_tokens)
        total += result.tokens_generated
        if result.valid:
            return Decoded(result.tokens, result.text, attempt, total, True)
    return Decoded((), b"", max_attempts, total, False)


@dataclass(frozen=True)
class CostPoint:
    alpha: float
    validity_rate: float
    retry_mean_tokens: float
    retry_mean_attempts: float
    retry_failures: int
    constrained_mean_tokens: float
    token_ratio: float

    def as_markdown_row(self) -> str:
        rate = f"{self.validity_rate * 100:.1f}%"
        ratio = "n/a" if self.retry_failures else f"{self.token_ratio:.2f}x"
        return (
            f"| {self.alpha:.2f} | {rate} | {self.retry_mean_attempts:.2f} | "
            f"{self.retry_mean_tokens:.1f} | {self.constrained_mean_tokens:.1f} | "
            f"{ratio} | {self.retry_failures} |"
        )


def cost_curve(engine: Engine, base: BigramModel, aligned: BigramModel,
               alphas: list[float], *, samples: int = 200, seed: int = 20240607,
               max_attempts: int = 50) -> list[CostPoint]:
    """Sweep model/schema alignment and measure retry cost against constrained cost.

    `alpha` interpolates from a model that ignores the schema to one that
    usually satisfies it, which is the only honest way to answer "is retrying
    cheaper?" -- the answer depends entirely on where on this curve you sit.
    """
    from .model import mix

    points: list[CostPoint] = []
    for alpha in alphas:
        model = mix(aligned, base, alpha)
        rng = SplitMix64(seed)
        valid = 0
        for _ in range(samples):
            if decode_unconstrained(engine, model, rng).valid:
                valid += 1

        rng = SplitMix64(seed ^ 0xA5A5)
        retry_tokens = 0
        retry_attempts = 0
        failures = 0
        for _ in range(samples):
            r = decode_retry(engine, model, rng, max_attempts=max_attempts)
            retry_tokens += r.tokens_generated
            retry_attempts += r.attempts
            if not r.valid:
                failures += 1

        rng = SplitMix64(seed ^ 0x5A5A)
        constrained_tokens = 0
        for _ in range(samples):
            constrained_tokens += decode_constrained(engine, model, rng).tokens_generated

        mean_retry = retry_tokens / samples
        mean_constrained = constrained_tokens / samples
        points.append(CostPoint(
            alpha=alpha,
            validity_rate=valid / samples,
            retry_mean_tokens=mean_retry,
            retry_mean_attempts=retry_attempts / samples,
            retry_failures=failures,
            constrained_mean_tokens=mean_constrained,
            token_ratio=mean_retry / mean_constrained if mean_constrained else float("inf"),
        ))
    return points
