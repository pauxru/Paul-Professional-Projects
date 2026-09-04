"""Corpus construction and the invariants that make its labels trustworthy.

Each assertion here corresponds to a way the experiment could have been quietly
wrong. They are not defensive coding; they are the reason the labels can be
called exact.
"""

from __future__ import annotations

import pytest

from rqlab.corpus import SINGLETON_FAMILIES, Placement, Span, build_corpus
from rqlab.distractors import assert_no_leaked_values, build_distractors
from rqlab.facts import assert_values_are_distinctive, build_facts
from rqlab.queries import (
    assert_queries_do_not_quote_answers,
    build_queries,
)


# -- Span algebra ----------------------------------------------------------

def test_span_rejects_inverted_bounds():
    with pytest.raises(ValueError):
        Span(10, 4)


def test_span_allows_empty():
    assert Span(4, 4).end == 4


def test_span_covered_by_exact():
    assert Span(5, 10).covered_by((Span(5, 10),))


def test_span_covered_by_superset():
    assert Span(5, 10).covered_by((Span(0, 20),))


def test_span_not_covered_when_one_character_short():
    # This is exactly the defect the chunker once had: a window trimmed to the
    # last token dropped the terminal period and failed containment by one
    # character, which read as a chunking failure.
    assert not Span(5, 10).covered_by((Span(5, 9),))
    assert not Span(5, 10).covered_by((Span(6, 10),))


def test_span_covered_by_adjacent_pair_when_contiguous():
    assert Span(0, 10).covered_by((Span(0, 5), Span(5, 10)))


def test_span_not_covered_by_pair_with_a_gap():
    assert not Span(0, 10).covered_by((Span(0, 4), Span(5, 10)))


def test_span_not_covered_by_unsorted_gapped_pair():
    assert not Span(0, 10).covered_by((Span(6, 10), Span(0, 4)))


def test_span_covered_by_unsorted_contiguous_pair():
    assert Span(0, 10).covered_by((Span(5, 10), Span(0, 5)))


def test_span_covered_by_overlapping_pair():
    assert Span(0, 10).covered_by((Span(0, 7), Span(3, 10)))


def test_span_not_covered_by_empty_tuple():
    assert not Span(0, 1).covered_by(())


# -- Placements ------------------------------------------------------------

def test_placement_requires_answer_and_context():
    p = Placement("f", "d", "l", Span(10, 20), (Span(0, 5),))
    assert set(p.required) == {Span(10, 20), Span(0, 5)}


def test_placement_not_covered_when_context_missing(corpus):
    p = next(pl for pl in corpus.placements if pl.context)
    assert not p.covered_by((p.answer,))
    assert p.covered_by(p.required)


def test_placement_with_no_context_is_covered_by_answer_alone():
    p = Placement("f", "d", "l", Span(0, 5), ())
    assert p.covered_by((Span(0, 5),))


# -- Facts -----------------------------------------------------------------

def test_facts_are_built_once_and_are_nonempty():
    facts = build_facts()
    assert len(facts) >= 60


def test_fact_ids_are_unique():
    facts = build_facts()
    assert len({f.fid for f in facts}) == len(facts)


def test_fact_sentence_contains_its_value_but_not_its_qualifier():
    from rqlab.text import analysed

    checked = 0
    for f in build_facts():
        assert f.value in f.sentence
        label = analysed(f.qualifier_label)
        if not label:
            continue
        # The qualifier lives in a heading, never in the sentence. This is the
        # mechanism the whole lab depends on: a chunk can hold the answer and
        # still not say what it applies to. Compared on analysed tokens, not
        # substrings -- "us" is inside "customer".
        assert not set(label) <= set(analysed(f.sentence)), f.fid
        checked += 1
    assert checked > 40


def test_values_are_distinctive():
    assert_values_are_distinctive()


def test_a_bare_numeric_value_would_be_rejected():
    from rqlab.facts import _distinctive

    assert not _distinctive("4")
    assert not _distinctive("16 ")
    assert _distinctive("4 concurrent jobs")
    assert _distinctive("99.0%")


# -- Corpus ----------------------------------------------------------------

def test_corpus_document_ids_unique(corpus):
    ids = [d.doc_id for d in corpus.documents]
    assert len(set(ids)) == len(ids)


def test_every_placement_points_at_a_real_document(corpus):
    ids = {d.doc_id for d in corpus.documents}
    for p in corpus.placements:
        assert p.doc_id in ids


def test_every_placement_span_lies_inside_its_document(corpus):
    for p in corpus.placements:
        text = corpus.document(p.doc_id).text
        for s in p.required:
            assert 0 <= s.start < s.end <= len(text)


def test_answer_span_actually_contains_the_value(corpus):
    by_fid = {f.fid: f for f in build_facts()}
    for p in corpus.placements:
        text = corpus.document(p.doc_id).text
        excerpt = text[p.answer.start : p.answer.end]
        assert by_fid[p.fid].value in excerpt


def test_context_span_actually_contains_the_qualifier(corpus):
    by_fid = {f.fid: f for f in build_facts()}
    checked = 0
    for p in corpus.placements:
        if not p.context:
            continue
        text = corpus.document(p.doc_id).text
        joined = " ".join(text[s.start : s.end] for s in p.context).lower()
        assert by_fid[p.fid].qualifier_label.lower() in joined
        checked += 1
    assert checked > 0


def test_singleton_families_are_placed_once_per_document(corpus):
    for p in corpus.placements:
        family = p.fid.split("::")[0]
        if family in SINGLETON_FAMILIES:
            same = [
                q for q in corpus.placements
                if q.doc_id == p.doc_id and q.fid.split("::")[0] == family
            ]
            assert len(same) == 1


def test_redundancy_is_at_least_one_for_every_placed_fact(corpus):
    for p in corpus.placements:
        assert corpus.redundancy(p.fid) >= 1


def test_corpus_spans_more_than_one_layout_for_most_facts(corpus):
    fids = {p.fid for p in corpus.placements}
    multi = sum(1 for f in fids if corpus.redundancy(f) > 1)
    assert multi > 0


def test_build_corpus_is_deterministic():
    a, b = build_corpus(), build_corpus()
    assert [d.text for d in a.documents] == [d.text for d in b.documents]
    assert a.placements == b.placements


# -- Distractors -----------------------------------------------------------

def test_distractors_are_deterministic():
    assert [d.text for d in build_distractors()] == [
        d.text for d in build_distractors()
    ]


def test_distractors_do_not_leak_any_governed_value(distractors):
    assert_no_leaked_values(distractors)


def test_distractor_leak_check_actually_catches_a_leak(distractors):
    from rqlab.corpus import Document

    value = build_facts()[0].value
    poisoned = distractors + (
        Document("poison", "Poison", "distractor", f"Note: {value} applies."),
    )
    with pytest.raises(AssertionError):
        assert_no_leaked_values(poisoned)


def test_distractor_ids_do_not_collide_with_answer_documents(corpus, distractors):
    assert not ({d.doc_id for d in distractors} & {d.doc_id for d in corpus.documents})


# -- Queries ---------------------------------------------------------------

def test_queries_do_not_quote_answers():
    assert_queries_do_not_quote_answers()


def test_query_ids_unique(queries):
    assert len({q.qid for q in queries}) == len(queries)


def test_every_query_has_at_least_one_target(queries):
    for q in queries:
        assert q.targets


def test_query_targets_are_real_facts(queries):
    fids = {f.fid for f in build_facts()}
    for q in queries:
        for t in q.targets:
            assert t in fids


def test_query_siblings_never_include_a_target(queries):
    for q in queries:
        assert not (set(q.siblings) & set(q.targets))


def test_query_split_is_a_stable_two_way_partition(queries):
    halves = {q.qid: q.split() for q in queries}
    assert set(halves.values()) == {"tune", "report"}
    again = {q.qid: q.split() for q in build_queries()}
    assert halves == again


def test_query_split_is_roughly_balanced(queries):
    tune = sum(1 for q in queries if q.split() == "tune")
    assert abs(tune - len(queries) / 2) < len(queries) * 0.15


def test_query_classes_are_all_populated(queries):
    from rqlab.queries import QUERY_CLASSES, by_class

    grouped = by_class()
    for c in QUERY_CLASSES:
        assert grouped[c], c
