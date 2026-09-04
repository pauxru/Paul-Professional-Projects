# ADR-002 — Money as `long` minor units, never `double`

- **Status**: Accepted
- **Date**: 2026
- **Context tags**: correctness, money, rounding

## Context

Money arithmetic must be **exact**. Binary floating-point (`double`/`float`) cannot represent most
decimal fractions (`0.1 + 0.2 != 0.3`), so any floating-point money path accumulates rounding drift
that eventually becomes a real, visible discrepancy in a ledger that must sum to zero. Even `decimal`,
while base-10, invites accidental sub-unit precision and needs a companion scale/currency to be
meaningful.

## Options considered

1. **`double` / `float`** for amounts.
2. **`decimal`** for amounts.
3. **Integer `long` minor units** with an explicit `Currency` carrying a `Scale` (e.g. KES/USD/EUR =
   2 decimal places → 100 minor units per major).

## Decision

Represent all money as a **`long` count of minor units** paired with a `Currency`, via a
`readonly record struct Money`. There is **no floating-point type anywhere in the domain**. Arithmetic
is `checked` integer math (overflow throws); mixing currencies throws `MixedCurrencyException`;
`Money.FromMajor(decimal)` rejects any value finer than the currency's scale
(`ledger.sub_minor_precision`). Every persisted monetary column is a `long`.

## Consequences

**Positive**
- Exact arithmetic — no representational rounding error; trial balance ties to the minor unit.
- Currency and scale are explicit and always travel with the amount, so cross-currency mistakes are
  caught, not silently coerced.
- Overflow is detected (`checked`) instead of wrapping.
- FX conversion can be done with exact rational arithmetic and a conserved remainder (see the FX
  service), which is impossible to guarantee with `double`.

**Negative / costs**
- Callers work in minor units (100000 = 1,000.00 KES); helper conversions (`FromMajor`/`ToMajor`) are
  provided but the mental model is "integers".
- A fixed, seeded currency set is required so scale is always known.

## Risks and mitigations

- **Risk**: a very large aggregate overflows `long`. **Mitigation**: `checked` arithmetic throws
  rather than wrapping; `long` covers ~9.2×10¹⁸ minor units, far beyond any realistic balance.
- **Risk**: an unknown currency with a different scale. **Mitigation**: `Currency.FromCode` rejects
  unknown currencies; only KES/USD/EUR (scale 2) are supported.

## Alternatives not chosen

- *`double`* — rejected outright: inexact, the classic money bug.
- *`decimal`* — rejected as the storage/domain type: better than `double` but still allows
  accidental sub-unit precision, needs an explicit scale/currency anyway, and is not a natural fit for
  the exact-remainder FX arithmetic. `decimal` is used only transiently at the `FromMajor` boundary to
  parse human input, then validated and converted to `long`.
