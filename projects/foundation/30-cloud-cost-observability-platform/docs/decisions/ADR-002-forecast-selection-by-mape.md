# ADR-002: Choose the default forecast by measured MAPE

## Context
Run-rate is simple but cannot distinguish weekends and recurring seasonal behavior. A choice made only by intuition is not defensible.

## Options
1. Always use simple run-rate.
2. Always use linear regression.
3. Backtest multiple methods and select lowest measured MAPE.

## Decision
Backtest run-rate, day-of-week seasonal-aware, and linear regression forecasts against historical months using the first 14 days as the observation point. Select the lowest measured MAPE as the default and return all methods with confidence intervals.

## Consequences
The chosen method can change as data changes. Current deterministic synthetic evaluation selects seasonal-aware forecasting at 0.63% MAPE.

## Risks
Historical MAPE does not guarantee a structural-break forecast. The API presents a band and anomaly detection separately identifies breaks.

## Alternatives
External forecasting services were rejected because the project must run with no infrastructure or provider calls.
