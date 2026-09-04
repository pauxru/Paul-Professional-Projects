# ADR-001: Support Request-Based and Window-Based SLIs

## Context
Availability can be measured as the fraction of successful requests or as the fraction of entirely healthy minutes. These describe different user harm and must not be silently conflated.

## Options
1. Support request-based SLIs only.
2. Support window-based SLIs only.
3. Declare the aggregation mode per SLI and evaluate each explicitly.

## Decision
Choose option 3. Request-based SLI sums good/valid events. Window-based SLI groups filtered samples by UTC minute and counts a minute good only when all relevant events/probes are good.

## Consequences
Service owners can make a product-specific choice and the UI/result carries the same terminology. The implementation needs tested denominators and minute grouping.

## Risks
Pre-aggregated telemetry could hide a partial minute or differing probe cadence. In production, ingestion contracts must identify source resolution and aggregation semantics.

## Alternatives
A future version could support ratio-of-ratios or composite indicators, but those require equally explicit weighting rules.
