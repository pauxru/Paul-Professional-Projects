# ADR-001 — Minor-unit money and explicit rounding

## Context

Billing calculations must be reproducible across runtimes and must not expose binary floating-point error. KES, USD, and EUR examples use two-decimal minor units.

## Options

1. Binary floating-point amounts.
2. `decimal` major-unit persistence.
3. Signed `long` minor-unit persistence with `decimal` used only for ratios/rates.

## Decision

Persist and expose money as a signed `long` plus ISO currency. Arithmetic verifies matching currencies and uses checked integer operations. Percentage, tax, and proration ratios use `decimal`, then explicitly round to a minor unit. Banker's rounding (`ToEven`) is the default because repeated midpoint calculations are less biased; away-from-zero is available where a commercial rule requires it.

## Consequences

Values are compact, exact at the storage boundary, JSON contracts are unambiguous, and negative adjustments are natural. Callers must know each currency's minor-unit exponent; this demonstration limits currencies to KES/USD/EUR.

## Risks

Very large extended quantities can overflow and are rejected rather than silently wrap. A broader currency set would require an ISO exponent catalogue, including zero- and three-decimal currencies.

## Alternatives

`decimal` persistence is viable for general commerce but allows inconsistent scale and rounding points. Arbitrary precision would add complexity not justified by the supported currencies.
