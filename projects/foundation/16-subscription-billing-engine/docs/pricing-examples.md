# Pricing Examples

All examples use signed minor units. `USD 10.00` is stored as `1000`. Unless stated otherwise, midpoint rounding is banker's rounding (`ToEven`). Negative one-off amounts are credits.

## Flat recurring

Configuration: monthly fee `USD 100.00` (`10000`).

| Quantity | Formula | Charge |
|---:|---|---:|
| 0 | fixed fee | `10000` |
| 1 | fixed fee | `10000` |
| 500 | fixed fee | `10000` |

The quantity does not scale this strategy.

## Per-unit

Configuration: `USD 0.25` (`25`) per seat.

| Units | Formula | Charge |
|---:|---|---:|
| 0 | `0 × 25` | `0` |
| 1 | `1 × 25` | `25` |
| 100 | `100 × 25` | `2500` |
| 101 | `101 × 25` | `2525` |

## Cumulative tiered

Configuration:

- units 1–100: `10` each
- units 101–200: `8` each
- units 201+: `5` each

For 250 units:

```text
first 100  = 100 × 10 = 1000
next 100   = 100 ×  8 =  800
remaining  =  50 ×  5 =  250
total                   = 2050 minor units
```

Boundaries:

| Units | Charge |
|---:|---:|
| 0 | 0 |
| 1 | 10 |
| 100 | 1000 |
| 101 | 1008 |
| 200 | 1800 |
| 201 | 1805 |

## Volume

Use the same tiers, but all units receive the price of the reached tier.

| Units | Reached rate | Formula | Charge |
|---:|---:|---|---:|
| 0 | n/a | zero usage | 0 |
| 1 | 10 | `1 × 10` | 10 |
| 100 | 10 | `100 × 10` | 1000 |
| 101 | 8 | `101 × 8` | 808 |
| 200 | 8 | `200 × 8` | 1600 |
| 201 | 5 | `201 × 5` | 1005 |

The discontinuity at 101 is deliberate and differs from cumulative tiering.

## Graduated with included allowance and overage

Configuration: base fee `USD 50.00` (`5000`), 100 included requests, overage `3` minor units per request.

| Units | Formula | Charge |
|---:|---|---:|
| 0 | base only | 5000 |
| 100 | base only | 5000 |
| 101 | `5000 + (1 × 3)` | 5003 |
| 250 | `5000 + (150 × 3)` | 5450 |

## Package/block

Configuration: blocks of 10 units cost `USD 10.00` (`1000`). Partial blocks round up.

| Units | Blocks | Charge |
|---:|---:|---:|
| 0 | 0 | 0 |
| 1 | 1 | 1000 |
| 10 | 1 | 1000 |
| 11 | 2 | 2000 |
| 20 | 2 | 2000 |
| 21 | 3 | 3000 |

The integer formula is `units / blockSize + (remainder == 0 ? 0 : 1)`.

## One-off charge and credit

- Setup charge: `+2500` produces a `USD 25.00` line.
- Service correction: `-2500` produces a `USD 25.00` credit line.

If a finalized adjustment invoice is net negative, the invoice amount due is zero and the negative balance creates an immutable credit note plus account credit for future FIFO application.

## Proration example

Old monthly amount: `10000`; new amount: `20000`; period is 31 days; change occurs after 15 days, leaving 16 days.

```text
remaining ratio = 16 / 31
old credit       = round(-10000 × 16/31) = -5161
intended net     = round((20000 - 10000) × 16/31) = 5161
new charge       = intended net - old credit = 10322
check            = -5161 + 10322 = 5161
```

The new charge is derived last so the two lines always equal the intended net, even at midpoint rounding boundaries.

## Tax and discount interaction

Exclusive KE example:

```text
taxable recurring subtotal = 10000
10% coupon                  = -1000
tax base                    =  9000
VAT 16%                     = +1440
pre-credit amount due       =  9440
FIFO account credit         = -2000
final amount due            =  7440
```

Inclusive KE example: a discounted gross of `10440` already contains tax. Extracted VAT is `10440 - round(10440 / 1.16) = 1440`; it is reported but not added again.

For mixed proration lines, signed taxable credits reduce the tax base. Tax rounding can be configured per line or once at invoice level. For two `3`-minor-unit lines at 16%, line rounding yields `0 + 0`, while invoice rounding yields `round(6 × 0.16) = 1`.

## Usage rounding example

A meter with increment `0.1 GB` and round-up mode treats each increment as one billable unit. Aggregated `1.21 GB / 0.1 = 12.1` rounds up to 13 billable increments. The plan's unit price is therefore the price per `0.1 GB` increment.
