# ADR-003: Robust seasonal anomaly detection instead of static thresholds

## Context
Cloud spend naturally varies by weekday, month-end batch windows, and growth trends. A fixed percentage threshold creates alert fatigue.

## Options
1. Static daily percentage thresholds.
2. One global z-score.
3. Seasonal references plus rolling MAD, z-score, EWMA, and CUSUM/change-point signals.

## Decision
Use day-of-week seasonal references, rolling median/MAD robust scores, conventional z-score, day-of-week EWMA, CUSUM, trend-change scoring, grouping, and planned-event suppression. Recurrent month-end batch behavior is treated as calendar seasonality after observation.

## Consequences
Signals include observed versus expected cost and a contributing dimension, rather than opaque threshold breaches. Known maintenance/migration events can remain visible but suppressed.

## Risks
Sensitivity is a calibration choice and high cardinality needs pre-aggregation in a real warehouse. The selected default is evaluated against known synthetic labels.

## Alternatives
Naïve threshold-only detection was rejected because it incorrectly flags predictable seasonality.
