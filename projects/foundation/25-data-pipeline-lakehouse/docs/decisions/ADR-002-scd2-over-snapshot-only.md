# ADR-002: Slowly Changing Dimension Type 2 for customers and products (not snapshot-only)

- Status: Accepted
- Date: 2026-09
- Deciders: Solo engineer (self-directed case study)

## Context

Customers and products change over time: a customer moves segment/city/loyalty tier, a product's price
changes. Facts (order lines, sessions) must be analysable **as the world was at the time the event
happened** — e.g. revenue attributed to the customer segment and product price that were in effect on
the order date, not today's values. The source emits a CDC change feed (insert/update/delete with a
`sequence` and `commitTs`), and updates can arrive **late and out of order**.

## Options considered

- **A. Type 1 (overwrite) / current-snapshot only.** Keep one row per business key with the latest
  attributes. Simple, small. But it destroys history: a fact built later cannot recover the attribute
  values that were valid at event time; cohort and price-mix analysis become wrong or impossible.
- **B. Full daily snapshots of every dimension.** Keep a copy of each dimension per day and join facts
  to the snapshot for the event date. Conceptually simple but storage grows with (rows x days), the
  join is coarse (day-grained), and it still needs logic to pick the right snapshot.
- **C. SCD Type 2.** One row per version of a business key with `valid_from` / `valid_to` /
  `is_current`. Facts join the version whose validity interval contains the event timestamp. Compact,
  exact to the instant, and the standard dimensional-modelling answer.

## Decision

Implement **SCD Type 2** (`Scd2Processor`) for `dim_customer` and `dim_product`, and build facts with
an **effective-version join**: `DimensionResolver.Effective(versions, key, eventTs)` selects the
version with `valid_from <= eventTs < valid_to` (`valid_to` exclusive; null = open/current).

The processor is deterministic and correct under disorder: it re-sorts the whole change log, applies
the highest-`sequence` change per instant, de-duplicates no-material-change updates, closes the open
version on delete, and derives content-stable surrogate keys from `businessKey | valid_from`.

## Consequences

- Positive: facts are historically accurate; the classic bug ("join to the current version") is
  avoided and **explicitly tested** (`GoldTests.Fact_joins_dimension_version_effective_at_order_time`,
  `DimensionJoinTests`, `Scd2Tests`) including out-of-order updates.
- Positive: surrogate keys decouple the star schema from natural keys and are stable across re-runs.
- Negative: more moving parts than Type 1 (validity intervals, open-segment handling, effective-time
  join). This complexity is the point of the exercise and is contained in two small, tested classes.

## Risks

- **Late-arriving dimension members** — an order can reference a customer that was deleted/rejected and
  is therefore absent from the conformed dimension. Handled by inferring a dimension member at gold
  (see ADR-003), not by failing the build.
- Boundary correctness of the interval join (inclusive `valid_from`, exclusive `valid_to`). Mitigated
  by `DimensionResolver` being a pure function with dedicated boundary tests.

## Alternatives not chosen

Type 1 (A) loses history and produces wrong time-aware analytics. Snapshot-only (B) is storage-heavy
and only day-grained. Both were rejected because the headline analytical questions (cohorts, price mix,
segment revenue over time) require point-in-time-correct dimensions.
