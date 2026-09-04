"""Chunking, and the tiling invariant that caught a real defect.

The chunkers once trimmed each window to the last *token*, which dropped the
sentence's terminal period and made exact span containment fail by one
character. It read as "structural chunking loses half the answers". The tell
was that the severed count was implausibly low -- if the chunker were really
severing answers from headings, severance would be high, not near zero.

``test_windows_tile_without_gaps`` is the assertion that would have caught it
on the first run.
"""

from __future__ import annotations

import pytest

from rqlab.chunking import CHUNKERS, chunk_all
from rqlab.corpus import Document, Span
from rqlab.coverage import (
    compute_coverage,
    document_level_relevance,
    partial_coverage,
    placement_survival,
)
from rqlab.text import tokens_with_offsets


@pytest.mark.parametrize("name", list(CHUNKERS))
def test_chunker_produces_chunks(name, answer_chunkings):
    assert answer_chunkings[name]


@pytest.mark.parametrize("name", list(CHUNKERS))
def test_chunk_ids_are_unique(name, chunkings):
    ids = [c.chunk_id for c in chunkings[name]]
    assert len(set(ids)) == len(ids)


@pytest.mark.parametrize("name", [n for n in CHUNKERS if n != "structural_prefixed_240"])
def test_chunk_text_matches_its_spans(name, chunkings, all_docs):
    by_id = {d.doc_id: d for d in all_docs}
    for c in chunkings[name][:400]:
        joined = "".join(by_id[c.doc_id].text[s.start : s.end] for s in c.spans)
        assert c.text == joined


def test_prefixed_chunk_text_is_deliberately_not_a_document_substring(
    answer_chunkings, corpus
):
    """The one chunker whose indexed text is synthesised rather than sliced.

    Its spans still describe what the chunk *covers* -- that is what coverage
    is computed from -- but the text handed to the index has the heading path
    rendered into it, so the two are not equal. Asserting this keeps the
    exception explicit rather than letting a future refactor silently make the
    invariant hold for the wrong reason.
    """
    by_id = {d.doc_id: d for d in corpus.documents}
    differing = 0
    for c in answer_chunkings["structural_prefixed_240"]:
        joined = "".join(by_id[c.doc_id].text[s.start : s.end] for s in c.spans)
        if c.text != joined:
            differing += 1
    assert differing > 0


@pytest.mark.parametrize("name", list(CHUNKERS))
def test_chunk_spans_lie_inside_their_document(name, chunkings, all_docs):
    by_id = {d.doc_id: d for d in all_docs}
    for c in chunkings[name]:
        n = len(by_id[c.doc_id].text)
        for s in c.spans:
            assert 0 <= s.start <= s.end <= n


@pytest.mark.parametrize("name", list(CHUNKERS))
def test_every_document_is_chunked(name, chunkings, all_docs):
    covered = {c.doc_id for c in chunkings[name]}
    assert covered == {d.doc_id for d in all_docs}


@pytest.mark.parametrize("name", ("fixed_120", "fixed_240"))
def test_fixed_windows_tile_the_document_exactly(name, corpus):
    """Disjoint fixed windows must partition the document with no gap at all.

    A gap here is invisible in aggregate metrics -- it just looks like slightly
    worse retrieval -- which is why it has to be asserted rather than
    inspected.
    """
    fn = CHUNKERS[name]
    for doc in corpus.documents:
        boundaries = sorted((s.start, s.end) for c in fn(doc) for s in c.spans)
        cursor = 0
        for start, end in boundaries:
            assert start <= cursor, f"{name}/{doc.doc_id}: gap before {start}"
            cursor = max(cursor, end)
        assert cursor == len(doc.text), f"{name}/{doc.doc_id}: tail lost"


@pytest.mark.parametrize("name", list(CHUNKERS))
def test_no_content_character_falls_outside_every_chunk(name, corpus):
    """The invariant that actually matters for labelling.

    Block-based chunkers legitimately drop the blank line *between* paragraphs,
    so exact tiling is too strong for them. What must never happen is that a
    character carrying content is uncovered, because a labelled span could
    contain it.
    """
    fn = CHUNKERS[name]
    for doc in corpus.documents:
        covered = bytearray(len(doc.text))
        for c in fn(doc):
            for s in c.spans:
                for i in range(s.start, s.end):
                    covered[i] = 1
        missing = [
            i for i, ch in enumerate(doc.text) if not ch.isspace() and not covered[i]
        ]
        assert not missing, f"{name}/{doc.doc_id}: uncovered at {missing[:5]}"


@pytest.mark.parametrize("name", list(CHUNKERS))
def test_no_labelled_span_straddles_a_chunk_gap(name, corpus):
    """A gap is only harmless if no label can fall into it."""
    fn = CHUNKERS[name]
    by_doc: dict[str, list] = {}
    for doc in corpus.documents:
        by_doc[doc.doc_id] = fn(doc)
    for p in corpus.placements:
        chunks = by_doc[p.doc_id]
        for span in p.required:
            covered = set()
            for c in chunks:
                for s in c.spans:
                    if s.start < span.end and span.start < s.end:
                        covered.update(range(max(s.start, span.start),
                                             min(s.end, span.end)))
            assert len(covered) == span.end - span.start, f"{name}/{p.fid}"


@pytest.mark.parametrize("name", list(CHUNKERS))
def test_no_token_is_split_across_a_boundary(name, corpus):
    fn = CHUNKERS[name]
    for doc in list(corpus.documents)[:6]:
        token_spans = {(s, e) for _, s, e in tokens_with_offsets(doc.text)}
        for c in fn(doc):
            for span in c.spans:
                for ts, te in token_spans:
                    straddles_start = ts < span.start < te
                    straddles_end = ts < span.end < te
                    assert not straddles_start
                    assert not straddles_end


def test_fixed_chunker_respects_its_token_budget(corpus):
    for doc in corpus.documents:
        for c in CHUNKERS["fixed_240"](doc):
            assert c.n_tokens <= 240


def test_overlap_chunker_produces_more_chunks_than_the_disjoint_one(answer_chunkings):
    assert len(answer_chunkings["overlap_240_120"]) > len(answer_chunkings["fixed_240"])


def test_prefixed_chunker_has_the_same_count_as_the_unprefixed_one(answer_chunkings):
    """The controlled comparison in section 1a depends on this being exact."""
    assert len(answer_chunkings["structural_prefixed_240"]) == len(
        answer_chunkings["structural_240"]
    )


def test_prefixed_chunks_carry_two_span_groups_where_a_heading_applies(
    answer_chunkings,
):
    multi = [c for c in answer_chunkings["structural_prefixed_240"] if len(c.spans) > 1]
    assert multi, "no chunk carried a propagated heading"


def test_prefixed_chunk_text_starts_with_its_heading_path(answer_chunkings, corpus):
    from rqlab.chunking import _sections

    paths = {
        s.heading_text
        for d in corpus.documents
        for s in _sections(d)
        if s.heading_text
    }
    checked = 0
    for c in answer_chunkings["structural_prefixed_240"]:
        if len(c.spans) < 2:
            continue
        assert any(c.text.startswith(p) for p in paths)
        checked += 1
    assert checked > 0


def test_sentence_window_produces_the_most_chunks(chunkings):
    counts = {k: len(v) for k, v in chunkings.items()}
    assert max(counts, key=counts.get) == "sentence_window_1"


def test_chunking_is_deterministic(corpus):
    docs = list(corpus.documents)
    for name, fn in CHUNKERS.items():
        a = [(c.chunk_id, c.text) for c in chunk_all(docs, fn)]
        b = [(c.chunk_id, c.text) for c in chunk_all(docs, fn)]
        assert a == b, name


def test_chunk_all_preserves_document_order(corpus):
    docs = list(corpus.documents)
    chunks = chunk_all(docs, CHUNKERS["fixed_240"])
    seen: list[str] = []
    for c in chunks:
        if not seen or seen[-1] != c.doc_id:
            seen.append(c.doc_id)
    assert seen == [d.doc_id for d in docs]


def test_empty_document_yields_no_chunks():
    doc = Document("empty", "Empty", "test", "")
    for name, fn in CHUNKERS.items():
        assert fn(doc) == [], name


def test_single_sentence_document_yields_one_chunk_for_fixed():
    doc = Document("tiny", "Tiny", "test", "One short sentence here.")
    assert len(CHUNKERS["fixed_240"](doc)) == 1


# -- Coverage --------------------------------------------------------------

@pytest.mark.parametrize("name", list(CHUNKERS))
def test_coverage_only_names_chunks_that_exist(name, answer_chunkings, corpus):
    chunks = answer_chunkings[name]
    ids = {c.chunk_id for c in chunks}
    cov = compute_coverage(corpus, chunks)
    for fid, cids in cov.by_fact.items():
        assert cids <= ids, fid


@pytest.mark.parametrize("name", list(CHUNKERS))
def test_covering_chunks_really_contain_the_required_spans(
    name, answer_chunkings, corpus
):
    chunks = answer_chunkings[name]
    by_id = {c.chunk_id: c for c in chunks}
    cov = compute_coverage(corpus, chunks)
    for fid, cids in cov.by_fact.items():
        placements = corpus.placements_for(fid)
        for cid in cids:
            ch = by_id[cid]
            assert any(
                p.doc_id == ch.doc_id and p.covered_by(ch.spans) for p in placements
            )


def test_prefixed_structural_covers_everything_the_plain_one_does(
    answer_chunkings, corpus
):
    plain = compute_coverage(corpus, answer_chunkings["structural_240"])
    pref = compute_coverage(corpus, answer_chunkings["structural_prefixed_240"])
    assert set(plain.by_fact) <= set(pref.by_fact)


def test_partial_coverage_is_disjoint_from_full_coverage(answer_chunkings, corpus):
    for name, chunks in answer_chunkings.items():
        cov = compute_coverage(corpus, chunks)
        part = partial_coverage(corpus, chunks)
        for fid, cids in part.items():
            assert not (cids & cov.relevant_chunks(fid)), f"{name}/{fid}"


def test_partial_coverage_is_empty_for_the_prefixed_chunker(answer_chunkings, corpus):
    part = partial_coverage(corpus, answer_chunkings["structural_prefixed_240"])
    assert sum(len(v) for v in part.values()) == 0


def test_partial_coverage_is_nonempty_for_the_plain_structural_chunker(
    answer_chunkings, corpus
):
    part = partial_coverage(corpus, answer_chunkings["structural_240"])
    assert sum(len(v) for v in part.values()) > 0


def test_document_level_relevance_is_a_superset_of_span_level(
    answer_chunkings, corpus
):
    for name, chunks in answer_chunkings.items():
        cov = compute_coverage(corpus, chunks)
        doc = document_level_relevance(corpus, chunks)
        for fid, cids in cov.by_fact.items():
            assert cids <= doc[fid], f"{name}/{fid}"


def test_ceiling_of_no_facts_is_zero(answer_chunkings, corpus):
    cov = compute_coverage(corpus, answer_chunkings["fixed_240"])
    assert cov.ceiling(()) == 0.0


def test_ceiling_is_between_zero_and_one(answer_chunkings, corpus):
    fids = tuple(sorted({p.fid for p in corpus.placements}))
    for name, chunks in answer_chunkings.items():
        c = compute_coverage(corpus, chunks).ceiling(fids)
        assert 0.0 <= c <= 1.0, name


# -- Placement survival ----------------------------------------------------

@pytest.mark.parametrize("name", list(CHUNKERS))
def test_survival_rows_account_for_every_placement(name, answer_chunkings, corpus):
    surv = placement_survival(corpus, answer_chunkings[name])
    total = sum(v[2] for v in surv.values())
    assert total == len(corpus.placements)
    for intact, severed, tot in surv.values():
        assert intact + severed <= tot
        assert min(intact, severed, tot) >= 0


def test_survival_intact_matches_coverage(answer_chunkings, corpus):
    for name, chunks in answer_chunkings.items():
        surv = placement_survival(corpus, chunks)
        intact = sum(v[0] for v in surv.values())
        by_doc: dict[str, list] = {}
        for ch in chunks:
            by_doc.setdefault(ch.doc_id, []).append(ch)
        expected = sum(
            1
            for p in corpus.placements
            if any(p.covered_by(c.spans) for c in by_doc.get(p.doc_id, ()))
        )
        assert intact == expected, name


def test_prefixed_chunker_severs_nothing(answer_chunkings, corpus):
    surv = placement_survival(corpus, answer_chunkings["structural_prefixed_240"])
    assert sum(v[1] for v in surv.values()) == 0


def test_plain_structural_severs_the_plan_matrix_layout(answer_chunkings, corpus):
    surv = placement_survival(corpus, answer_chunkings["structural_240"])
    intact, severed, total = surv["plan_matrix"]
    assert severed > 0
    assert intact < total


def test_severance_dominates_outright_loss(answer_chunkings, corpus):
    """The headline of section 1a, asserted rather than merely reported."""
    severed = lost = 0
    for chunks in answer_chunkings.values():
        for intact, sev, tot in placement_survival(corpus, chunks).values():
            severed += sev
            lost += tot - intact - sev
    assert severed > lost * 5


def test_span_of_never_returns_a_boundary_inside_the_text_gap():
    """Regression: boundaries sit at the start of the next token or at the end."""
    from rqlab.chunking import _span_of

    text = "alpha beta. gamma delta."
    toks = tokens_with_offsets(text)
    sp = _span_of(toks, 0, 2, len(text))
    assert isinstance(sp, Span)
    assert sp.start == 0
    assert text[sp.start : sp.end].startswith("alpha beta")
    # The boundary is the start of the next token, so the period survives.
    assert "." in text[sp.start : sp.end]
