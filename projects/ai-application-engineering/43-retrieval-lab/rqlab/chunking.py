"""Chunking strategies.

A chunker is the only stage in a retrieval pipeline whose mistakes are
unrecoverable. A retriever that ranks the right chunk 40th can be repaired by
a reranker; a chunker that separates an answer from the heading it depends on
has destroyed the information, and everything downstream is working from a
corpus in which the answer no longer exists.

Every chunk carries the character spans of the source document it was built
from. Two chunkers here produce text that is not a substring of the source --
``structural_prefixed`` repeats the heading path at the top of every chunk --
so provenance is a tuple of spans rather than one interval, and coverage of a
labelled answer is a containment test against their union.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Callable, Iterable

from .corpus import Document, Span
from .text import sentences_with_offsets, tokens_with_offsets

_HEADING = re.compile(r"^(#{1,6})\s+(.*)$", re.MULTILINE)


@dataclass(frozen=True)
class Chunk:
    chunk_id: str
    doc_id: str
    #: What the index sees. Not necessarily a substring of the document.
    text: str
    #: Where it came from. Coverage of a labelled span is tested against these.
    spans: tuple[Span, ...]

    @property
    def n_tokens(self) -> int:
        return len(tokens_with_offsets(self.text))


def _span_of(
    tokens: list[tuple[str, int, int]], lo: int, hi: int, text_len: int
) -> Span:
    """Provenance for the token window ``[lo, hi)``, as a source region.

    Boundaries are placed at the *start of the next token* rather than at the
    end of the last one. Trimming to the last token looks equivalent and is
    not: a sentence ends with punctuation, which is not a token, so a trimmed
    span stops one character short of the labelled answer and exact
    containment fails at every boundary that coincides with a sentence end.
    Placing boundaries at token starts also makes consecutive windows tile the
    document with no gaps, so no character is unattributable. See
    docs/portfolio/failure-log.md.
    """
    start = tokens[lo][1] if lo > 0 else 0
    end = tokens[hi][1] if hi < len(tokens) else text_len
    return Span(start, end)


def _windows(doc: Document, size: int, stride: int, name: str) -> list[Chunk]:
    toks = tokens_with_offsets(doc.text)
    if not toks:
        return []
    out: list[Chunk] = []
    i = 0
    n = len(toks)
    while i < n:
        j = min(i + size, n)
        sp = _span_of(toks, i, j, len(doc.text))
        out.append(
            Chunk(f"{doc.doc_id}#{name}:{len(out)}", doc.doc_id,
                  doc.text[sp.start : sp.end], (sp,))
        )
        if j >= n:
            break
        i += stride
    return out


def fixed(size: int) -> Callable[[Document], list[Chunk]]:
    def go(doc: Document) -> list[Chunk]:
        return _windows(doc, size, size, f"fx{size}")

    return go


def fixed_overlap(size: int, stride: int) -> Callable[[Document], list[Chunk]]:
    def go(doc: Document) -> list[Chunk]:
        return _windows(doc, size, stride, f"ov{size}_{stride}")

    return go


def _blocks(text: str) -> list[Span]:
    """Paragraph spans, then sentence spans inside oversized paragraphs."""
    spans: list[Span] = []
    cursor = 0
    for para in text.split("\n\n"):
        start = cursor
        end = cursor + len(para)
        if para.strip():
            lead = len(para) - len(para.lstrip())
            spans.append(Span(start + lead, end - (len(para) - len(para.rstrip()))))
        cursor = end + 2
    return spans


def recursive(size: int) -> Callable[[Document], list[Chunk]]:
    """Split on the largest natural boundary that fits, then merge greedily.

    The strategy most libraries default to. It respects paragraphs, which is
    why it does well on prose, and it has no concept of a heading, which is why
    it separates a value from the section that qualifies it.
    """

    def go(doc: Document) -> list[Chunk]:
        text = doc.text
        pieces: list[Span] = []
        for block in _blocks(text):
            body = text[block.start : block.end]
            toks = tokens_with_offsets(body)
            if len(toks) <= size:
                pieces.append(block)
                continue
            for s0, s1 in sentences_with_offsets(body):
                sent = Span(block.start + s0, block.start + s1)
                stoks = tokens_with_offsets(text[sent.start : sent.end])
                if len(stoks) <= size:
                    pieces.append(sent)
                    continue
                for k in range(0, len(stoks), size):
                    sub = stoks[k : k + size]
                    pieces.append(
                        Span(sent.start + sub[0][1], sent.start + sub[-1][2])
                    )

        out: list[Chunk] = []
        cur: list[Span] = []
        cur_n = 0
        for p in pieces:
            n = len(tokens_with_offsets(text[p.start : p.end]))
            if cur and cur_n + n > size:
                sp = Span(cur[0].start, cur[-1].end)
                out.append(
                    Chunk(f"{doc.doc_id}#rc{size}:{len(out)}", doc.doc_id,
                          text[sp.start : sp.end], (sp,))
                )
                cur, cur_n = [], 0
            cur.append(p)
            cur_n += n
        if cur:
            sp = Span(cur[0].start, cur[-1].end)
            out.append(
                Chunk(f"{doc.doc_id}#rc{size}:{len(out)}", doc.doc_id,
                      text[sp.start : sp.end], (sp,))
            )
        return out

    return go


@dataclass(frozen=True)
class _Section:
    heading_spans: tuple[Span, ...]
    heading_text: str
    body: Span


def _sections(doc: Document) -> list[_Section]:
    """Heading-delimited sections with the full ancestor heading path."""
    text = doc.text
    marks = [(m.start(), m.end(), len(m.group(1)), m.group(2)) for m in _HEADING.finditer(text)]
    if not marks:
        return [_Section((), "", Span(0, len(text)))]
    out: list[_Section] = []
    if marks[0][0] > 0 and text[: marks[0][0]].strip():
        out.append(_Section((), "", Span(0, marks[0][0])))
    stack: list[tuple[int, Span, str]] = []
    for idx, (hs, he, level, title) in enumerate(marks):
        while stack and stack[-1][0] >= level:
            stack.pop()
        stack.append((level, Span(hs, he), title))
        body_start = he
        body_end = marks[idx + 1][0] if idx + 1 < len(marks) else len(text)
        # A heading whose body is empty -- because a subheading follows it
        # immediately -- is still emitted, with a zero-length body. Dropping it
        # would delete the heading's words from the index entirely, which is a
        # vocabulary loss rather than the co-location effect the structural
        # chunkers exist to demonstrate. See docs/portfolio/failure-log.md.
        out.append(
            _Section(
                tuple(s for _, s, _ in stack),
                " > ".join(t for _, _, t in stack),
                Span(body_start, body_end),
            )
        )
    return out


def _split_body(text: str, body: Span, size: int) -> list[Span]:
    """Divide a section body into windows that tile it exactly.

    A body that fits is returned unchanged rather than trimmed to its first and
    last token, for the reason given in ``_span_of``.
    """
    toks = tokens_with_offsets(text[body.start : body.end])
    if not toks:
        return []
    if len(toks) <= size:
        return [body]
    out: list[Span] = []
    for k in range(0, len(toks), size):
        lo = body.start + toks[k][1] if k > 0 else body.start
        hi = body.start + toks[k + size][1] if k + size < len(toks) else body.end
        out.append(Span(lo, hi))
    return out


def structural(size: int) -> Callable[[Document], list[Chunk]]:
    """Never cross a heading. The heading itself starts the chunk.

    A chunk therefore *contains* the heading it sits under, so a value whose
    meaning depends on that heading survives -- but only for the first chunk of
    an oversized section. That partial success is visible in the attribution.
    """

    def go(doc: Document) -> list[Chunk]:
        out: list[Chunk] = []
        for sec in _sections(doc):
            own = sec.heading_spans[-1:] if sec.heading_spans else ()
            parts = _split_body(doc.text, sec.body, size)
            if not parts:
                if own:
                    sp = Span(own[0].start, sec.body.end)
                    out.append(
                        Chunk(f"{doc.doc_id}#st{size}:{len(out)}", doc.doc_id,
                              doc.text[sp.start : sp.end], (sp,))
                    )
                continue
            for i, p in enumerate(parts):
                if i == 0 and own:
                    sp = Span(own[0].start, p.end)
                    spans: tuple[Span, ...] = (sp,)
                    body_text = doc.text[sp.start : sp.end]
                else:
                    spans = (p,)
                    body_text = doc.text[p.start : p.end]
                out.append(
                    Chunk(f"{doc.doc_id}#st{size}:{len(out)}", doc.doc_id,
                          body_text, spans)
                )
        return out

    return go


def structural_prefixed(size: int) -> Callable[[Document], list[Chunk]]:
    """Repeat the full heading path at the top of every chunk of a section.

    The indexed text is no longer a substring of the document. That is the
    point: the chunk now carries the qualifying context even when the body has
    been split several windows away from its heading, so it is both retrievable
    by the qualifier and self-sufficient as an answer.
    """

    def go(doc: Document) -> list[Chunk]:
        out: list[Chunk] = []
        for sec in _sections(doc):
            parts = _split_body(doc.text, sec.body, size)
            if not parts:
                if sec.heading_spans:
                    out.append(
                        Chunk(
                            f"{doc.doc_id}#sp{size}:{len(out)}",
                            doc.doc_id,
                            sec.heading_text,
                            sec.heading_spans,
                        )
                    )
                continue
            for p in parts:
                body = doc.text[p.start : p.end]
                text = f"{sec.heading_text}\n{body}" if sec.heading_text else body
                out.append(
                    Chunk(
                        f"{doc.doc_id}#sp{size}:{len(out)}",
                        doc.doc_id,
                        text,
                        sec.heading_spans + (p,),
                    )
                )
        return out

    return go


def sentence_window(radius: int) -> Callable[[Document], list[Chunk]]:
    """One chunk per sentence, widened by ``radius`` neighbours on each side.

    Produces many small overlapping chunks. Precision of the retrieval unit is
    high and the number of units is large, which trades index cost for the
    chance that a boundary lands badly.
    """

    def go(doc: Document) -> list[Chunk]:
        sents = sentences_with_offsets(doc.text)
        out: list[Chunk] = []
        for i in range(len(sents)):
            lo = max(0, i - radius)
            hi = min(len(sents), i + radius + 1)
            sp = Span(sents[lo][0], sents[hi - 1][1])
            out.append(
                Chunk(f"{doc.doc_id}#sw{radius}:{i}", doc.doc_id,
                      doc.text[sp.start : sp.end], (sp,))
            )
        return out

    return go


#: The strategies compared in the report. Sizes are token counts under the
#: shared analyser, so "240" means the same thing to every strategy.
CHUNKERS: dict[str, Callable[[Document], list[Chunk]]] = {
    "fixed_120": fixed(120),
    "fixed_240": fixed(240),
    "overlap_240_120": fixed_overlap(240, 120),
    "recursive_240": recursive(240),
    "structural_240": structural(240),
    "structural_prefixed_240": structural_prefixed(240),
    "sentence_window_1": sentence_window(1),
}


def chunk_all(
    docs: Iterable[Document], strategy: Callable[[Document], list[Chunk]]
) -> list[Chunk]:
    out: list[Chunk] = []
    for d in docs:
        out.extend(strategy(d))
    return out
