# ADR-001 — Deterministic-first extraction with an optional LLM adapter

- **Status:** Accepted
- **Date:** 2026
- **Context tags:** extraction, AI guardrails, reproducibility

## Context

The platform must extract typed fields (invoice number, totals, line items, …) from documents whose
layouts vary by supplier. The obvious modern approach is a large language / vision model. But the
PRIME DIRECTIVE requires that `dotnet build` and `dotnet test` succeed on a machine with only the
.NET SDK — **zero network calls, no paid APIs** — and that measured accuracy/STP numbers be
reproducible run-to-run. A non-deterministic, network-bound extractor would violate both.

More fundamentally, the portfolio's thesis is that the defensible engineering is the **guardrail
layer**, not the model. That layer (validation, confidence, routing, review) must be exercised by a
predictable extractor so its behaviour is observable and testable.

## Options considered

1. **LLM/vision model as the default extractor.** Highest recall on messy layouts.
2. **Deterministic anchor/regex/positional + spatial table detection as default; LLM behind an
   optional adapter.** Reproducible and offline by default; model is opt-in.
3. **Deterministic only, no model seam at all.** Simplest, but fails to show the intended production
   extension point.

## Decision

Adopt **option 2**. Extraction defaults to `DeterministicFieldExtractor` (learned-anchor → anchor →
regex → positional strategies plus `SpatialTableDetector`). An `IChatModel`/`IDocumentClassifier`
adapter exists behind configuration for the classification path and is unit-tested with a stubbed
HTTP handler, but is **never** used by default. The same seam is the documented place a real
extraction model would attach.

## Consequences

- **Positive:** Fully offline, free, reproducible build and tests; every field carries an explainable
  strategy and evidence box; the guardrail layer is exercised deterministically; measured numbers are
  stable (extraction 98.78% over the corpus).
- **Positive:** The model is a configuration change, not a code change — the architecture is honest
  about where AI would plug in.
- **Negative:** Lower recall on genuinely messy real-world layouts than a strong model would achieve;
  the deterministic engine needs per-template anchors (mitigated by the feedback loop, ADR-004).

## Risks & mitigations

- *Risk:* readers assume the deterministic engine is the product. *Mitigation:* README and this ADR
  state plainly that the model seam is intentional and the guardrails are the point.
- *Risk:* anchor drift as templates change. *Mitigation:* the correction feedback loop learns new
  anchors per supplier (ADR-004).

## Alternatives not chosen

Option 1 was rejected because it breaks the offline/deterministic directive and hides the guardrail
story behind a black box. Option 3 was rejected because it removes the production-relevant extension
point that makes the design credible.
