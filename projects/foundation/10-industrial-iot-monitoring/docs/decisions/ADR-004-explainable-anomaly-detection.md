# ADR-004: Explainable statistical anomaly detection before ML

## Context
Operators need a reason for an alarm, and the demonstration has synthetic limited history rather than a labelled production data set.

## Options
1. Introduce an opaque ML model/library.
2. Ship no anomaly detection.
3. Use rolling z-score, MAD, EWMA trend deviation, and seasonal baseline comparisons.

## Decision
Choose option 3. Every detector returns its statistic and baseline terms: mean/standard deviation, median/MAD, EWMA/residual scale, or seasonal context. The simulator's injected faults provide deterministic validation cases.

## Consequences
Detection is portable, inspectable, and debuggable by an operator. It is also intentionally limited: statistical thresholds require tuning and cannot substitute for a validated safety system.

## Risks
Distribution changes, correlated metrics, or slow degradation may cause false positives/negatives. Measured synthetic rates are documented and not generalized to real plants.

## Alternatives
A production program could use per-asset labelled data, feature governance, drift monitoring, and a separately validated model service.
