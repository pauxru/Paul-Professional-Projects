# ADR-0005 — Explainability-first over black-box ML

**Status:** Accepted
**Date:** 2026-01-15

## Context

The industry conversation about fraud detection is dominated by machine learning. Gradient-boosted
trees on hand-crafted features are the workhorse; graph neural networks over transaction graphs are
the frontier. Compared to a rules engine, a well-trained model can catch more subtle patterns.

But there is a compounding cost that a portfolio case study should engage with honestly:

- **Analyst throughput** collapses when the model's reason codes are shallow. "Feature 27 exceeded
  threshold 0.6" is not something an investigator can defend to a chargeback board.
- **Regulator conversations** in payments (PSD2 in the EU, PCI in the US, CBK regulations in
  Kenya) demand a documented decision logic. Model cards help but do not answer the day-of question.
- **Feedback-loop debt**: every disposition needs to inform *something*. In a rules world, "this
  disposition fed rule X, which was tuned by Y percent" is legible. In a model world, the disposition
  goes into a re-training queue.

## Decision

Build the **rules-first system** as the primary decision path. Treat ML as an optional shadow
challenger — the plumbing (shadow slot, decision persistence, delta comparator) is deliberately
generic so an ML challenger could be added without touching the live path.

Explainability is a first-class output: every decision persists the feature vector used, the rules
fired, their contributions, and the ruleset version. Reproducibility is a **tested invariant**
(`ADR-0002`).

## Consequences

**Positive:**
- Every decline is defensible in a chargeback dispute — the reason codes are human sentences.
- Analyst tuning becomes a first-class workflow (`DetectionEvaluator.RecommendAsync`), not a
  mysterious "please retrain" ticket.
- The system is *smaller* — fewer moving parts, no training pipeline, no model store.

**Negative — and this is the honest bit:**
- Even with tuning, rules-based recall lags what a well-trained model would achieve on the same
  data. The shipped `v1.1.0` catches **55.7 %** of injected fraud on the seeded synthetic dataset
  at 92.2 % precision and 0.45 % FPR (baseline `v1.0.0` sat at 16 % / 100 % / 0 % — see
  `docs/detection-performance.md` for the full champion-vs-challenger + threshold-sweep
  comparison). Even the tuned operating point leaves 44 % of injected fraud uncaught, most of
  it in the takeover-burst family where a device-fingerprint / first-CNP-txn-on-device rule
  would be the next lift.
- Missed fraud that a model would catch is missed fraud. This is a real cost.
- The system does not learn without a human in the loop. The tuning loop is genuine — and I ran
  it end-to-end during the review pass — but its cadence is human-scale (days / weeks), not
  model-scale (hours).

**Where we would go next.** An ML challenger scored in shadow, using the same feature vector as
the rules engine. If the challenger's precision on labelled cases beats the rules by a defensible
margin *and* the analyst UI can show the rule-equivalent explanation, promote it.

## Alternatives considered

- **Rules + ML from day one.** Rejected for scope: doing either well is a lot; doing both
  simultaneously means doing neither.
- **Pure ML with SHAP explanations.** SHAP is a fine tool but its explanations require analyst
  training and don't answer the "which policy applied" question a regulator will actually ask.

## Related

- `src/FraudPipeline.Application/Rules/RuleEngine.cs`
- `src/FraudPipeline.Application/Feedback/DetectionEvaluator.cs`
- `docs/detection-performance.md`
