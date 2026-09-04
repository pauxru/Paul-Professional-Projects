# ADR-003 — Confidence aggregation method

- **Status:** Accepted
- **Date:** 2026
- **Context tags:** confidence, routing, STP, guardrails

## Context

Routing (auto-approve / review / reject) hinges on a single document-level confidence number derived
from many per-field confidences plus the classifier confidence and the validation outcome. The
aggregation function directly controls the **straight-through-processing rate** and, more importantly,
the **safety** of auto-approval. A naive mean would let many well-read fields mask one badly-read
critical field — exactly the case that causes wrong payments.

## Options considered

1. **Arithmetic mean** of required-field confidences × classifier confidence.
2. **Minimum** (weakest-link only).
3. **Weakest-link blended with the mean**, then penalised for validation failures/warnings.

## Decision

Adopt **option 3**, implemented in the pure domain service `ConfidenceScoring`:

```
fieldComponent = 0.7 × min(requiredFieldConfidences) + 0.3 × mean(requiredFieldConfidences)
score          = fieldComponent × classifierConfidence
score         -= 0.35 × hardFailureCount
score         -= 0.08 × warningCount
score          = clamp(score, 0, 1)   // empty required set → 0
```

`RoutingPolicy` then rejects below 0.30, auto-approves at/above 0.85 **and** only when there is no
hard validation failure, otherwise routes to review.

## Consequences

- **Positive:** One weak critical field dominates the score (70% weight on the minimum), so it cannot
  be masked — auto-approval is conservative and safe.
- **Positive:** Validation results feed confidence directly, and a hard failure independently blocks
  auto-approval, giving two lines of defence.
- **Positive:** Deterministic and pure → boundary behaviour is pinned by unit tests
  (0.85 → auto-approve, 0.30 → review, 0.2999 → reject).
- **Negative:** The STP rate is lower than a mean-based aggregation would report (measured 31.58% on a
  deliberately mixed corpus). This is an accepted trade of throughput for safety.

## Risks & mitigations

- *Risk:* the fixed weights (0.7/0.3, 0.35, 0.08) are somewhat arbitrary. *Mitigation:* they are
  centralised, documented and test-pinned; they are tuning knobs, not scattered magic numbers.
- *Risk:* mis-calibrated per-field confidences skew the document score. *Mitigation:* confidences are
  strategy-derived (learned-anchor 0.98 → positional 0.58) and observable per field for auditing.

## Alternatives not chosen

Option 1 (mean) is unsafe: it masks weak critical fields. Option 2 (pure minimum) is too brittle — a
single slightly-soft non-critical field would needlessly suppress otherwise-clean documents; blending
30% of the mean restores sensible behaviour while keeping the weakest-link safety property.
