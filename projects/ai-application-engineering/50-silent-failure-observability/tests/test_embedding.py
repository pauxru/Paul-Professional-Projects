"""Tests for the hashing-trick embedding.

The properties that matter here are the ones the whole project rests on: the embedding is
a deterministic, stable function of the text, it is not sensitive to the process it runs
in, and caching it does not change it.
"""

from __future__ import annotations

import numpy as np
import pytest

from src import embedding


def test_embedding_is_unit_length():
    v = embedding.embed("the quick brown fox jumps over the lazy dog")
    assert np.isclose(np.linalg.norm(v), 1.0)


def test_embedding_has_expected_dimensions():
    assert embedding.embed("hello").shape == (embedding.DIMENSIONS,)


def test_embedding_is_deterministic_within_a_process():
    assert np.array_equal(embedding.embed("a stable string"), embedding.embed("a stable string"))


def test_embedding_does_not_use_pythons_salted_hash():
    """The literal reason `_hash` exists.

    `hash()` is salted per process, so an embedding built on it would produce different
    vectors on every run and every number in this project would be unreproducible. This
    checks the digest directly rather than trusting the docstring.
    """
    assert embedding._hash("abcd") == embedding._hash("abcd")
    assert embedding._hash("abcd") != embedding._hash("abce")
    # A fixed expectation, so a change of hash function is a test failure and not a
    # silent change to every number in docs/results.md.
    assert embedding._hash("abcd") == 17303320624217654311


def test_empty_text_embeds_to_zeros_not_nan():
    v = embedding.embed("")
    assert np.all(np.isfinite(v))
    assert float(np.linalg.norm(v)) == 0.0


def test_cosine_of_zero_vector_is_zero_not_nan():
    z = np.zeros(embedding.DIMENSIONS)
    assert embedding.cosine(z, embedding.embed("anything")) == 0.0


def test_cosine_of_identical_text_is_one():
    v = embedding.embed("refund processed for order 12345")
    assert np.isclose(embedding.cosine(v, v), 1.0)


def test_similar_texts_are_closer_than_dissimilar_ones():
    a = embedding.embed("your refund of $42.00 has been issued to the original card")
    b = embedding.embed("your refund of $58.00 has been issued to the original card")
    c = embedding.embed("the warranty on this product expired eleven months ago")
    assert embedding.cosine(a, b) > embedding.cosine(a, c)


def test_cached_vectors_are_read_only():
    """A caller that mutated a cached vector would corrupt every later use of that string."""
    v = embedding.embed("do not mutate me")
    with pytest.raises(ValueError):
        v[0] = 99.0


def test_cache_returns_the_same_values_as_the_uncached_path():
    for text in ("short", "a considerably longer piece of text with punctuation, and digits 123"):
        assert np.array_equal(
            embedding.embed(text), embedding._embed_uncached(text, embedding.DIMENSIONS)
        )


def test_ngrams_pad_so_edge_characters_are_not_underweighted():
    grams = embedding.ngrams("abc", n=2)
    assert grams[0].startswith(" ")
    assert grams[-1].endswith(" ")


def test_ngrams_normalise_whitespace():
    assert embedding.ngrams("a  b\tc") == embedding.ngrams("a b c")


def test_ngrams_are_case_insensitive():
    assert embedding.ngrams("Refund") == embedding.ngrams("REFUND")


def test_ngrams_of_text_shorter_than_n_returns_one_gram():
    assert len(embedding.ngrams("a", n=32)) == 1


def test_embed_all_shape():
    m = embedding.embed_all(["one", "two", "three"])
    assert m.shape == (3, embedding.DIMENSIONS)


def test_embed_all_of_empty_iterable_is_well_shaped():
    m = embedding.embed_all([])
    assert m.shape == (0, embedding.DIMENSIONS)


def test_mean_pairwise_cosine_of_identical_rows_is_one():
    v = embedding.embed("identical")
    m = np.vstack([v, v, v])
    assert np.isclose(embedding.mean_pairwise_cosine(m), 1.0)


def test_mean_pairwise_cosine_needs_two_rows():
    assert embedding.mean_pairwise_cosine(embedding.embed_all(["only one"])) == 0.0


def test_mean_pairwise_cosine_excludes_the_diagonal():
    """With the diagonal included, three orthogonal rows would score 1/3 instead of 0."""
    m = np.eye(3)
    assert np.isclose(embedding.mean_pairwise_cosine(m), 0.0)


def test_a_mixture_of_two_clusters_is_less_self_similar_than_one():
    """The measurement that overturned `answer_diversity`'s founding hypothesis.

    If this ever stops holding, the two-sided scoring in that detector is no longer
    justified and the essay about it is wrong.
    """
    cluster_a = ["your refund of $%d has been issued to the original card" % i for i in range(20)]
    cluster_b = ["I'm sorry, I'm not able to help with that request." for _ in range(20)]
    one = embedding.mean_pairwise_cosine(embedding.embed_all(cluster_a))
    mixed = embedding.mean_pairwise_cosine(embedding.embed_all(cluster_a[:10] + cluster_b[:10]))
    assert mixed < one


def test_sign_hash_prevents_all_positive_drift():
    """Without the sign hash every collision reinforces and long documents drift to the
    all-positive corner. A long document should have both signs present."""
    v = embedding.embed(" ".join(f"token{i}" for i in range(400)))
    assert (v > 0).any() and (v < 0).any()


def test_project_selects_columns():
    m = embedding.embed_all(["a", "b", "c"])
    assert embedding.project(m, (0, 3)).shape == (3, 2)
