# ADR-005: Minor-Units Money Representation

## Status

Accepted, dated 2026-09.

## Context

Reconciliation compares, sums, and explains monetary values across records, matches, exceptions, and reports. Domain entities store amounts as `long` minor units: `ReconRecord.AmountMinor`, `ReconRecord.FeeMinor`, `Match.InternalAmountMinor`, `Match.ExternalAmountMinor`, `Match.ExpectedFeeMinor`, `Match.FeeVarianceMinor`, and `ReconciliationException.AmountMinor`.

The `Money` value object wraps `long MinorUnits` plus an ISO currency code. It normalises currency to uppercase, exposes `ToMajor`, creates values from major units through `FromMajor`, and combines values only when currencies match. `CurrencyInfo` defines minor-unit exponents for KES/USD/EUR/GBP as 2, JPY as 0, BHD as 3, and unknown currencies as 2. `FromMajor` uses `MidpointRounding.ToEven` (banker's rounding). Cross-currency `Add`, `Subtract`, and difference operations throw.

## Decision

Represent money internally as `(long minor units, ISO currency code)` through the `Money` value object and persist monetary fields as minor-unit `long` columns plus separate currency fields. Convert human-readable major amounts only at ingestion, reporting, or display boundaries. Use `CurrencyInfo` exponents for scale, banker's rounding when converting major to minor, and fail fast on cross-currency arithmetic.

Never use `double` for money.

## Options Considered

1. `long` minor units plus ISO currency.
   - Pros: exact integer arithmetic; compact storage; stable equality and hashing; natural fit for reconciliation and balance assertions.
   - Cons: display conversion is required; exponent metadata must be maintained; extreme aggregate sums need checked arithmetic awareness.
2. `decimal` major units plus ISO currency.
   - Pros: readable values in debugger and database; base-10 representation avoids binary floating-point surprises.
   - Cons: equality and rounding boundaries are easier to mishandle; database scale choices become part of correctness; minor-unit comparison is less direct.
3. `double` major units.
   - Pros: convenient and fast for general numeric calculations.
   - Cons: binary floating-point error is unacceptable for financial reconciliation; equality, sums, and balance assertions become unsafe.
4. Store amounts as strings.
   - Pros: preserves original textual input exactly.
   - Cons: poor arithmetic and indexing; parsing errors move deeper into the system; unsuitable for matching and reporting.

## Consequences

Positive consequences:

- Balance assertions and match comparisons use exact integer sums.
- KES, USD, and EUR fit the fictional project context while `CurrencyInfo` also proves support for zero-decimal JPY and three-decimal BHD.
- Cross-currency arithmetic fails immediately instead of producing meaningless totals.
- Banker's rounding is explicit and centralised in `Money.FromMajor`.

Negative consequences:

- Developers must remember that persisted amounts are minor units, not display amounts.
- Unknown currencies default to 2 decimals, which may be wrong for a real unsupported currency.
- Very large checked additions can throw instead of wrapping, requiring callers to handle overflow if volumes or amounts grow dramatically.

## Risks

- Risk: a caller treats `AmountMinor` as a major amount in API or report code. Mitigation: keep naming explicit with the `Minor` suffix and use `Money.ToMajor` at boundaries.
- Risk: unsupported currency exponent defaults to 2 and creates incorrect conversions. Mitigation: validate ingestion with `CurrencyInfo.IsKnown` where supported currencies are required and add exponents before enabling new currencies.
- Risk: rounding disputes at half-minor values. Mitigation: document and test `MidpointRounding.ToEven` as the single rounding policy.

## Alternatives

A reviewer might expect `decimal` because .NET financial applications commonly use it. It was not chosen as the internal representation because this engine's core operations are reconciliation equality, tolerance checks, subset sums, fee variance, and balance assertions. Integer minor units make those operations deterministic. `decimal` remains acceptable at the boundary when parsing or displaying major amounts, but not as the persisted or matching representation.
