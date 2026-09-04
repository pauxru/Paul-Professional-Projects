# Database / Star-Schema Reference

This document describes the **gold** star schema that is served through SQLite. Bronze and silver are
intermediate medallion layers on the filesystem lake (see the README and `docs/lineage.md`); only the
gold serving tables listed here are projected into the query engine.

> Storage note: the lake stores `Decimal` and `Timestamp` as text to preserve precision/round-trip;
> when loaded into SQLite, `Decimal` columns have **TEXT** affinity and `Timestamp` columns are ISO-8601
> text. Cast in SQL where you need numeric ordering on a decimal column.

## Grain statements (the most important lines in this file)

- **`fact_order_line`** — grain: **one row per order line** (`order_line_id`). Additive measures:
  `quantity`, `gross_amount`, `net_amount`, `net_amount_usd`. `net_amount_usd` is `net_amount`
  converted with the FX rate effective on the order date.
- **`fact_clickstream_session`** — grain: **one row per web session** (`session_id`). Measures:
  `event_count`, `page_views`, `add_to_carts`, `checkouts`, `purchases`, plus a `converted` flag.
- **`agg_daily_revenue`** — grain: **one row per calendar day** (`date_key`). Pre-aggregated from
  `fact_order_line`.
- **`agg_cohort_retention`** — grain: **one row per (acquisition cohort month, activity month)**.
- **`agg_funnel`** — grain: **one row per funnel step**.
- **`agg_inventory_position`** — grain: **one row per product** (current on-hand position as of the
  latest movement).

## Keys & relationships

- Surrogate keys (`customer_sk`, `product_sk`) are integers derived from the SCD2 business key +
  `valid_from`, stable across re-runs.
- Facts reference the **dimension version effective at the event time** (SCD2), not the current version.
- `dim_date` uses an integer `date_key` (`yyyymmdd`); facts carry `order_date_key` / `session_date_key`.
- Referential integrity is enforced by the gold quality gate; late/deleted dimension members are
  **inferred** so every fact key resolves (`is_inferred = true` marks them).

## ER diagram

```mermaid
erDiagram
    dim_customer ||--o{ fact_order_line : "customer_sk"
    dim_product  ||--o{ fact_order_line : "product_sk"
    dim_date     ||--o{ fact_order_line : "order_date_key"
    dim_channel  ||--o{ fact_order_line : "channel"
    dim_currency ||--o{ fact_order_line : "currency"
    dim_customer ||--o{ fact_clickstream_session : "customer_sk"
    dim_date     ||--o{ fact_clickstream_session : "session_date_key"
    dim_channel  ||--o{ fact_clickstream_session : "channel"
    fact_order_line ||--o{ agg_daily_revenue : "rolled up by date_key"

    dim_customer {
        long customer_sk PK
        string customer_id
        string name
        string email
        string city
        string country
        string currency
        string segment
        string loyalty_tier
        timestamp valid_from
        timestamp valid_to
        bool is_current
        bool is_inferred
    }
    dim_product {
        long product_sk PK
        string product_id
        string name
        string category
        decimal unit_price
        string currency
        bool active
        timestamp valid_from
        timestamp valid_to
        bool is_current
        bool is_inferred
    }
    dim_date {
        long date_key PK
        string date
        long year
        long quarter
        long month
        long day
        long day_of_week
        string day_name
        bool is_weekend
    }
    dim_currency {
        string currency PK
        decimal rate_to_usd
        bool is_base
    }
    dim_channel {
        string channel PK
        string description
    }
    fact_order_line {
        string order_line_id PK
        string order_id
        long order_date_key FK
        long customer_sk FK
        long product_sk FK
        string channel FK
        string currency FK
        long quantity
        decimal unit_price
        decimal discount
        decimal gross_amount
        decimal net_amount
        decimal net_amount_usd
    }
    fact_clickstream_session {
        string session_id PK
        long customer_sk FK
        long session_date_key FK
        string channel FK
        timestamp start_ts
        timestamp end_ts
        long event_count
        long page_views
        long add_to_carts
        long checkouts
        long purchases
        bool converted
    }
    agg_daily_revenue {
        long date_key PK
        long orders
        long lines
        long units
        decimal revenue_usd
    }
    agg_cohort_retention {
        string cohort_month
        string activity_month
        long months_since
        long customers
    }
    agg_funnel {
        long step_order
        string step
        long sessions
    }
    agg_inventory_position {
        string product_id PK
        long position
        string as_of_date
    }
```

## Serving tables loaded into SQLite (`GoldServingTables`)

`dim_customer`, `dim_product`, `dim_date`, `dim_currency`, `dim_channel`, `fact_order_line`,
`fact_clickstream_session`, `agg_daily_revenue`, `agg_cohort_retention`, `agg_funnel`,
`agg_inventory_position` (11 tables). Bronze/silver/quarantine are **not** loaded — that is the PII
boundary described in the security review.

## Indexes

The serving tables are rebuilt per pipeline run and are small (demo marts), so no secondary indexes are
created; SQLite scans the marts directly within the row-cap/timeout guards. Primary-key columns above
are logical (dimensional) keys — see ADR-005 for why SQLite (not a partitioned warehouse) is the
serving engine, and its limits.
