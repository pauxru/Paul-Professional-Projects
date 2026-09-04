# ADR-002: Use Multi-Window, Multi-Burn-Rate Alerting

## Context
Alerting on raw error percentage ignores SLO target and window duration. Alerting on one burn window either pages after recovery or reacts to small bursts.

## Options
1. Static error-rate threshold.
2. One burn-rate threshold per SLO.
3. Long-window burn threshold AND short-window burn threshold.

## Decision
Choose option 3, with 14.4×/1h AND 6×/5m, 6×/6h AND 3×/30m, and 3×/1d AND 1×/3d defaults. The formulas and hand-computed tests are in `docs/slo-mathematics.md`.

## Consequences
Page urgency maps to intentional budget spend while a current short window confirms active impact. Alert state can recover cleanly and record detection lag.

## Risks
Default thresholds assume a 30-day interpretation when explaining intended spend. Teams must tune notification routing and rule selection to their SLO window.

## Alternatives
Adaptive anomaly detection may supplement this later, but should not replace an explainable budget-based paging contract.
