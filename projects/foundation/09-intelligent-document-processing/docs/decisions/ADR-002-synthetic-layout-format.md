# ADR-002 — Synthetic `.ocr.json` layout format vs real OCR/PDF

- **Status:** Accepted
- **Date:** 2026
- **Context tags:** ingestion, spatial extraction, dependencies

## Context

To detect tables and locate fields spatially, the extractor needs word-level **coordinates**, which
normally come from an OCR engine or PDF text layer. Real OCR/PDF libraries are heavy native
dependencies, are not guaranteed to be available on the build host (no Docker, no Python), and would
make the corpus — and therefore the measured accuracy — non-deterministic.

## Options considered

1. **Bundle a real OCR/PDF library** (e.g. a Tesseract or PDF-text wrapper) and ship sample PDFs.
2. **Define a small synthetic OCR output format** (`.ocr.json`: pages of word boxes with `x, y,
   width, height, page`) and generate a deterministic corpus in it; also accept `.txt`/`.csv` by
   synthesising a layout.
3. **Use plain text only**, abandoning spatial features.

## Decision

Adopt **option 2**. The synthetic `.ocr.json` format models exactly the primitive a real OCR engine
emits — a stream of positioned words — so the `SpatialTableDetector` and positional extraction
strategies operate on real coordinates. `DocumentGenerator` produces the corpus deterministically
from supplier templates (including degraded variants), and `LayoutSynthesizer` gives `.txt`/`.csv`
uploads a positional layout so they flow through the same code path.

## Consequences

- **Positive:** No native dependencies; build/test stay offline and deterministic; the spatial code is
  genuinely exercised and unit-tested; the corpus and its ground truth are versioned in-repo,
  enabling honest accuracy measurement.
- **Positive:** A real OCR adapter can implement `IDocumentParser` and emit the same word-box model,
  so nothing downstream changes.
- **Negative:** The system does not prove it works on real scans/PDFs; OCR-specific noise is only
  *simulated* (character confusions `O/0`, `l/1`, `rn/m`), not produced by a real engine.

## Risks & mitigations

- *Risk:* the format looks like a toy. *Mitigation:* it is deliberately isomorphic to real OCR output
  (word + bounding box + page), and the degraded corpus injects realistic OCR error classes.
- *Risk:* over-fitting extraction to the synthetic layouts. *Mitigation:* strategies are generic
  (anchors/regex/coordinate clustering), not template-hardcoded, and degraded variants stress them.

## Alternatives not chosen

Option 1 breaks the dependency-free, deterministic directive and risks an unbuildable project. Option
3 throws away the spatial table detection that is central to real invoice extraction.
