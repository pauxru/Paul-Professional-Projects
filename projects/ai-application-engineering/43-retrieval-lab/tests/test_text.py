"""The analyser. Every span label in the lab is expressed in its offsets, so a
change here silently changes what "the chunk contains the answer" means.
"""

from __future__ import annotations

import pytest

from rqlab.text import (
    analysed,
    content_tokens,
    ngrams,
    normalise,
    sentences_with_offsets,
    stem,
    tokenize,
    tokens_with_offsets,
)


def test_normalise_case_folds_and_strips_accents():
    assert normalise("The QUICK Brown") == "the quick brown"
    assert normalise("Zürich café") == "zurich cafe"


def test_normalise_preserves_length_for_ascii():
    src = "Retention is 400 days."
    assert len(normalise(src)) == len(src)


def test_normalise_is_idempotent():
    once = normalise("Mixed   Case\n\nText")
    assert normalise(once) == once


@pytest.mark.parametrize(
    "raw,expected",
    [
        ("hello world", ["hello", "world"]),
        ("multi-part token", ["multi-part", "token"]),
        ("99.0% uptime", ["99", "0", "uptime"]),
        ("", []),
    ],
)
def test_tokenize(raw, expected):
    assert tokenize(raw) == expected


def test_token_offsets_recover_the_source_text():
    text = "Retention is 400 days before deletion."
    for tok, start, end in tokens_with_offsets(text):
        assert text[start:end].lower() == tok


def test_token_offsets_are_strictly_increasing():
    text = "One two three four five six seven."
    toks = tokens_with_offsets(text)
    for a, b in zip(toks, toks[1:]):
        assert a[2] <= b[1]
        assert a[1] < b[1]


def test_content_tokens_drop_stopwords():
    out = content_tokens("what is the retention for the enterprise plan")
    assert "the" not in out
    assert "is" not in out
    assert "retention" in out


def test_content_tokens_keep_a_query_that_is_all_stopwords_nonempty_only_if_present():
    assert content_tokens("of the and to") == []


@pytest.mark.parametrize(
    "word,expected",
    [
        ("retentions", "retention"),
        ("policies", "policy"),
        ("running", "running"),
        ("buckets", "bucket"),
        ("access", "access"),
    ],
)
def test_stem_examples(word, expected):
    assert stem(word) == expected


def test_stem_leaves_short_tokens_alone():
    # The length guard is why "days" survives intact. Stripping four-letter
    # words collides far more often than it conflates: "plans"/"plan" is a gain,
    # "gas"/"ga" and "bus"/"bu" are not.
    assert stem("days") == "days"
    assert stem("logs") == "logs"


def test_stem_does_not_strip_double_s():
    assert stem("access") == "access"
    assert stem("address") == "address"


def test_stem_is_idempotent():
    for w in ["retentions", "policies", "buckets", "regions", "access", "as"]:
        assert stem(stem(w)) == stem(w)


def test_stem_never_empties_a_short_token():
    for w in ["is", "as", "us", "s"]:
        assert stem(w) != ""


def test_analysed_is_normalise_tokenize_stop_stem():
    assert analysed("The RETENTION policies") == ["retention", "policy"]


def test_sentence_splitter_does_not_split_inside_an_abbreviation():
    text = "Use approx. 400 days. Then stop."
    assert len(sentences_with_offsets(text)) == 2


def test_ngrams():
    assert list(ngrams(["a", "b", "c"], 2)) == [("a", "b"), ("b", "c")]
    assert list(ngrams(["a"], 2)) == []
    assert list(ngrams([], 1)) == []


def test_sentence_offsets_tile_the_text_without_overlap():
    text = "First sentence. Second one! Third? Fourth."
    spans = sentences_with_offsets(text)
    assert len(spans) == 4
    for a, b in zip(spans, spans[1:]):
        assert a[1] <= b[0]


def test_sentence_offsets_cover_every_non_space_character():
    text = "Alpha beta. Gamma delta.\n\nEpsilon zeta."
    spans = sentences_with_offsets(text)
    covered = set()
    for s, e in spans:
        covered.update(range(s, e))
    missing = {i for i, ch in enumerate(text) if not ch.isspace()} - covered
    assert missing == set()


def test_sentence_offsets_on_text_without_terminator():
    spans = sentences_with_offsets("no terminator here")
    assert spans == [(0, len("no terminator here"))]


def test_sentence_offsets_empty():
    assert sentences_with_offsets("") == []
    assert sentences_with_offsets("   ") == []
