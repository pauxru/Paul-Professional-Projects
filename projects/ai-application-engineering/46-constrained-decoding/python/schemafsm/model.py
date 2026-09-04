"""A deterministic, finite-state stand-in for a language model.

No real LLM is used anywhere in this project. That is a deliberate design
choice, not a shortcut: the headline result here is an *exact* statement about
what constrained decoding does to a distribution, and you can only state it
exactly if the partition function is computable. A bigram model is the largest
model family for which that is true, so it is the one used.

The generator is a hand-written SplitMix64 rather than `random.Random` so the
numbers are byte-identical across Python versions and platforms.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Sequence

_MASK64 = (1 << 64) - 1

# Sentinel "previous token" for the start of a sequence.
BOS = -1


class SplitMix64:
    """The reference SplitMix64 generator. 64-bit, deterministic, portable."""

    def __init__(self, seed: int) -> None:
        self._s = seed & _MASK64

    def next_u64(self) -> int:
        self._s = (self._s + 0x9E3779B97F4A7C15) & _MASK64
        z = self._s
        z = ((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9) & _MASK64
        z = ((z ^ (z >> 27)) * 0x94D049BB133111EB) & _MASK64
        return z ^ (z >> 31)

    def next_float(self) -> float:
        """Uniform in [0, 1) using the top 53 bits, as is conventional."""
        return (self.next_u64() >> 11) * (1.0 / (1 << 53))


def _normalise(weights: Sequence[float]) -> list[float]:
    total = sum(weights)
    if total <= 0.0:
        n = len(weights)
        return [1.0 / n] * n
    return [w / total for w in weights]


@dataclass(frozen=True)
class BigramModel:
    """P(next | previous) over `vocab_size` tokens plus one EOS slot.

    Rows are indexed by the previous token id, with BOS stored in row
    `vocab_size`. Column `vocab_size` is EOS. Every row sums to 1.
    """

    vocab_size: int
    rows: tuple[tuple[float, ...], ...]

    @property
    def eos_id(self) -> int:
        return self.vocab_size

    def _row_index(self, prev: int) -> int:
        return self.vocab_size if prev == BOS else prev

    def next_distribution(self, prev: int) -> tuple[float, ...]:
        return self.rows[self._row_index(prev)]

    def prob(self, prev: int, token: int) -> float:
        return self.rows[self._row_index(prev)][token]

    def logits(self, prev: int) -> list[float]:
        """Log-probabilities, which is what a decoder would actually receive."""
        import math

        return [math.log(p) if p > 0.0 else -math.inf for p in self.next_distribution(prev)]


def random_bigram(vocab_size: int, seed: int, *, eos_bias: float = 0.15,
                  concentration: float = 1.0) -> BigramModel:
    """A random bigram model.

    `concentration` < 1 produces peakier rows (raising uniform draws to a power
    greater than one before normalising), which is a cheap way to sweep from a
    near-uniform model to a confident one without introducing a temperature at
    sample time.
    """
    rng = SplitMix64(seed)
    exponent = 1.0 / max(concentration, 1e-9)
    rows: list[tuple[float, ...]] = []
    for _ in range(vocab_size + 1):
        raw = [rng.next_float() ** exponent + 1e-9 for _ in range(vocab_size)]
        raw.append(eos_bias * (sum(raw) / max(vocab_size, 1)) * vocab_size)
        rows.append(tuple(_normalise(raw)))
    return BigramModel(vocab_size=vocab_size, rows=tuple(rows))


def mix(a: BigramModel, b: BigramModel, alpha: float) -> BigramModel:
    """alpha * a + (1 - alpha) * b, row-wise.

    Used to sweep a model continuously from "knows nothing about the schema" to
    "almost always emits valid output", which is the x-axis of the
    retry-versus-constrain cost comparison.
    """
    if a.vocab_size != b.vocab_size:
        raise ValueError("models must share a vocabulary")
    rows = tuple(
        tuple(alpha * x + (1.0 - alpha) * y for x, y in zip(ra, rb))
        for ra, rb in zip(a.rows, b.rows)
    )
    return BigramModel(vocab_size=a.vocab_size, rows=rows)


def sample(model: BigramModel, prev: int, rng: SplitMix64,
           allowed: Sequence[int] | None = None) -> int:
    """Draw one token, optionally restricted to `allowed` and renormalised.

    This is exactly the "locally renormalise over the mask" step whose
    distortion the experiments quantify.
    """
    dist = model.next_distribution(prev)
    if allowed is None:
        indices = range(len(dist))
        weights = list(dist)
    else:
        indices = list(allowed)
        weights = [dist[i] for i in indices]
    total = sum(weights)
    if total <= 0.0:
        raise ValueError("no probability mass on the allowed set")
    u = rng.next_float() * total
    acc = 0.0
    idx = list(indices)
    for i, w in zip(idx, weights):
        acc += w
        if u < acc:
            return i
    return idx[-1]
