# ADR-005 — Use decimal money and final-installment reconciliation

## Context
Loan schedules often fail operationally at minor-unit rounding boundaries. A schedule whose displayed installments do not equal total principal plus interest is not acceptable.

## Options
1. Use `double` and round displayed amounts later.
2. Round every calculated value without reconciling the last payment.
3. Use `decimal`, round to two minor units at installment boundaries, and adjust the final installment to remaining principal and interest.

## Decision
Use option 3. Both flat and reducing schedules track remaining amounts and set the final installment from the remainder. Fees are classified as capitalized or deducted. APR uses a decimal Newton-Raphson IRR solver with a bounded bisection fallback.

## Consequences
Schedules sum exactly and tests cover zero/one-term and rounding-residue cases. Calculations assume currencies with two minor units, which matches the seeded KES/USD products.

## Risks
Very long or extreme cash-flow patterns may need more robust numerical diagnostics. APR is a disclosure calculation, not a substitute for jurisdiction-specific regulatory disclosure rules.

## Alternatives
Minor-unit `long` storage or a dedicated money package would be appropriate for a ledger-grade system; this prototype keeps `decimal` domain values and explicit rounding boundaries.
