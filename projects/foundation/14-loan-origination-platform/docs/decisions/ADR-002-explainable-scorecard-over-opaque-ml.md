# ADR-002 — Prefer an explainable weighted scorecard to opaque ML

## Context
The fictional lender needs a defensible explanation for every risk recommendation. A black-box model might produce a convenient number without showing the contribution of arrears, affordability, collateral, or bureau information.

## Options
1. Return a fabricated “AI credit score.”
2. Use an opaque machine-learning model.
3. Use a transparent weighted scorecard with versioned factors and bands.

## Decision
Use option 3 as the default `IRiskScorer`. Six explicit factors produce a 0–100 score, A–E band, and contribution breakdown. The deterministic bureau simulator is only one input and is never presented as a real bureau decision.

## Consequences
Underwriters can see why a band changed and tests can pin each boundary. The result is explainable and suitable for a regulatory-style demonstration, without claiming regulatory approval or fairness validation.

## Risks
Weights are illustrative rather than empirically calibrated, and transparent rules can be gamed. Real deployment would require model risk management, bias testing, monitoring, and approved policy governance.

## Alternatives
An external scorer can implement `IRiskScorer` later, but it must return human-readable reason codes and retain its model/version metadata.
