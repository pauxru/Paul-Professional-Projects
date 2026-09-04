"""Deterministic text embeddings with no model and no dependencies beyond numpy.

Character n-gram feature hashing -- the "hashing trick". Each n-gram is hashed to a
column, signed by a second hash to cancel collisions in expectation, accumulated, and L2
normalised.

This is a real embedding algorithm, not a stand-in. It was the production answer for text
similarity before dense encoders, and it has the property this project needs: it is a
*function of the text*, so a degradation injected into the generated text shows up in the
vectors for the same reason it would with a transformer -- because the words changed.

What it does not have is semantics. It cannot tell that "refund" and "reimbursement" are
related. See docs/known-limitations.md for what that costs the conclusions.
"""

from __future__ import annotations

import hashlib
from functools import lru_cache
from typing import Iterable, Sequence

import numpy as np

DIMENSIONS = 256
NGRAM = 4


def _hash(token: str) -> int:
    """A stable hash. Python's builtin hash() is salted per process and would make every
    run of this project produce different vectors."""
    return int.from_bytes(hashlib.blake2b(token.encode("utf-8"), digest_size=8).digest(), "big")


def ngrams(text: str, n: int = NGRAM) -> list[str]:
    """Character n-grams over a normalised, space-padded string.

    Padding with a single space at each end means the first and last characters
    participate in the same number of n-grams as the middle ones, which stops short texts
    from being dominated by their interior.
    """
    normalised = " " + " ".join(text.lower().split()) + " "
    if len(normalised) < n:
        return [normalised]
    return [normalised[i : i + n] for i in range(len(normalised) - n + 1)]


def _embed_uncached(text: str, dimensions: int) -> np.ndarray:
    vector = np.zeros(dimensions, dtype=np.float64)
    if not text.strip():
        # An empty document has no content to represent. Without this guard the space
        # padding in `ngrams` gives it a single " " gram and it embeds to a perfectly
        # ordinary unit vector pointing at whatever column that gram hashes to -- so every
        # empty answer in a corpus would look identical to every other and unrelated to
        # nothing, which is a much worse lie than a zero vector. `cosine` guards zeros.
        return vector
    for gram in ngrams(text):
        h = _hash(gram)
        column = h % dimensions
        # The sign hash makes collisions cancel rather than accumulate. Without it, two
        # unrelated n-grams landing in the same column always reinforce, and the
        # embedding of a long document drifts towards the all-positive corner.
        sign = 1.0 if (h >> 32) & 1 else -1.0
        vector[column] += sign

    norm = float(np.linalg.norm(vector))
    if norm == 0.0:
        return vector
    return vector / norm


@lru_cache(maxsize=1 << 16)
def _embed_cached(text: str, dimensions: int) -> np.ndarray:
    vector = _embed_uncached(text, dimensions)
    vector.flags.writeable = False
    return vector


def embed(text: str, dimensions: int = DIMENSIONS) -> np.ndarray:
    """Embed one document. Returns a unit vector, or zeros for empty input.

    The result is memoised, which is sound because the function is pure: the same string
    always produces the same vector, by construction and by the use of a stable hash. It
    is also the difference between a five-minute run and a fifteen-second one -- the
    corpus is template-generated, so a ninety-day stream of 10,800 answers contains a few
    thousand distinct strings, and the panel embeds each stream five times over.

    Cached vectors are returned read-only. A caller that mutated one in place would
    silently corrupt every subsequent computation that touched the same string, and that
    is exactly the class of bug that takes a day to find.
    """
    return _embed_cached(text, dimensions)


def embed_all(texts: Iterable[str], dimensions: int = DIMENSIONS) -> np.ndarray:
    """Embed a batch. Shape (n, dimensions)."""
    rows = [embed(t, dimensions) for t in texts]
    if not rows:
        return np.zeros((0, dimensions), dtype=np.float64)
    return np.vstack(rows)


def cosine(a: np.ndarray, b: np.ndarray) -> float:
    """Cosine similarity. Unit vectors in, so this is a dot product, but the guard
    matters: an empty document embeds to zeros and would otherwise divide by zero."""
    na = float(np.linalg.norm(a))
    nb = float(np.linalg.norm(b))
    if na == 0.0 or nb == 0.0:
        return 0.0
    return float(np.dot(a, b) / (na * nb))


def mean_pairwise_cosine(matrix: np.ndarray) -> float:
    """Average cosine similarity between distinct rows.

    Used as a diversity statistic. The intuition it was built on -- that a model producing
    generic boilerplate has *higher* mean pairwise similarity -- turns out to be true only
    under total contamination. Under partial contamination the day contains two tight
    clusters instead of one and this number goes *down*. See `detectors.answer_diversity`.
    """
    n = matrix.shape[0]
    if n < 2:
        return 0.0
    gram = matrix @ matrix.T
    off_diagonal = gram.sum() - np.trace(gram)
    return float(off_diagonal / (n * (n - 1)))


def project(matrix: np.ndarray, axes: Sequence[int] = (0, 1)) -> np.ndarray:
    """Project onto named columns. Used only by the dashboard for a scatter plot."""
    return matrix[:, list(axes)]
