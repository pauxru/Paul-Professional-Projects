# Database Schema — Northstar Logistics (fictional)

## Storage choice
The default adapter is EF Core 10 with SQLite (`northstar-lab.db` for a Development run and SQLite in-memory for integration tests). `EnsureCreated` is used because migrations are not the teaching point of this lab; a production migration pipeline would replace it.

## Tables
| Table | Primary key | Important columns | Indexes / constraints |
|---|---|---|---|
| `Customers` | `Id` (GUID) | `Name` (120), `Email` (254) | unique `Email` |
| `Orders` | `Id` (GUID) | `CustomerId`, `Reference` (48), `Destination` (160), `Status`, `CreatedAt` (Unix ms), `Version` | unique `Reference`; index `CustomerId`; FK to `Customers`; concurrency token `Version` |
| `Shipments` | `Id` (GUID) | `OrderId`, `TrackingNumber` (64), `DispatchedAt` (Unix ms), `DeliveredAt` (Unix ms nullable) | unique `TrackingNumber`; unique `OrderId`; FK to `Orders` |

`DateTimeOffset` values are persisted as UTC Unix milliseconds because SQLite does not translate `DateTimeOffset` ordering directly. The application materializes them back into UTC `DateTimeOffset` values.

```mermaid
erDiagram
    CUSTOMERS ||--o{ ORDERS : books
    ORDERS ||--o| SHIPMENTS : dispatches
    CUSTOMERS {
        guid Id PK
        string Name
        string Email UK
    }
    ORDERS {
        guid Id PK
        guid CustomerId FK
        string Reference UK
        string Destination
        string Status
        long CreatedAt
        int Version
    }
    SHIPMENTS {
        guid Id PK
        guid OrderId FK_UK
        string TrackingNumber UK
        long DispatchedAt
        long DeliveredAt
    }
```

## Scenario-only data
`INC-001` creates in-memory `Orders`/`Shipments` tables to count EF commands. `INC-002` creates an in-memory `CargoEvents` table with a `LookupCode` predicate and captures `EXPLAIN QUERY PLAN` before/after creating `IX_CargoEvents_LookupCode`. These datasets are discarded at scenario completion and are not the sample API schema.
