# ADR-004 — Feedback loop as per-supplier hints vs model retraining

- **Status:** Accepted
- **Date:** 2026
- **Context tags:** feedback loop, learning, extraction

## Context

When a reviewer corrects an extracted field, the system should get better at the *same supplier's*
next document. The classic ML answer is to collect corrections as labels and periodically retrain a
model. But retraining is non-deterministic, needs infrastructure and data volume the platform does
not have, cannot be proven by a fast unit test, and does not fit the offline/deterministic directive.

## Options considered

1. **Accumulate corrections and retrain an extraction model** on a schedule.
2. **Learn per-supplier extraction hints** (anchors/positions) from each correction, applied
   immediately to that supplier's future documents.
3. **Do nothing** — corrections only fix the document in hand.

## Decision

Adopt **option 2**. On correction, `AnchorLearner` locates the corrected value in the document layout
and learns the **label word immediately to its left (or above)** as a `SupplierHint` for that field.
The next document from the same supplier template consults hints first; a matched hint extracts via
the `LearnedAnchor` strategy at confidence 0.98. Hints are reinforced when confirmed again.

This is proven end-to-end by `CorrectionFeedbackTests`: a v1 document with the value behind a
non-standard `Ref:` label is mis-extracted; the correction learns the `Ref` anchor; a v2 document of
the same template is then extracted correctly.

## Consequences

- **Positive:** Immediate improvement (no retraining latency); fully deterministic and unit-testable;
  explainable ("we learned the `Ref` anchor for supplier X"); requires no ML infrastructure.
- **Positive:** Aligns with the guardrail thesis — learning is transparent data, not an opaque model
  update.
- **Negative:** Learning is per-supplier and does not generalise across suppliers; a brand-new
  supplier gets no benefit until its first correction.

## Risks & mitigations

- *Risk:* a bad correction poisons a hint. *Mitigation:* corrections are captured with reviewer,
  reason and timestamp (auditable and reversible); hints are reinforced by repetition, so one-off
  anomalies carry little weight.
- *Risk:* template redesign invalidates hints. *Mitigation:* hints are anchored to label text, which
  survives moderate layout change; new corrections relearn as needed.

## Alternatives not chosen

Option 1 (retraining) violates the offline/deterministic directive, cannot be demonstrated by a fast
test, and is disproportionate to the corpus size. Option 3 wastes the single most valuable signal the
system produces — human corrections.
