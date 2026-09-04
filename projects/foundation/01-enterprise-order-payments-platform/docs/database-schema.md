# Database schema

All tables are created by `AppDbContext.OnModelCreating` via the
`IEntityTypeConfiguration<>` classes under
`src/Contoso.Payments.Infrastructure/Persistence/Configurations/`.

## Entity relationship diagram

```mermaid
erDiagram
    Product ||--o{ InventoryItem : "1 to 1 by ProductId"
    Product ||--o{ OrderLine : "referenced by OrderLine"
    Order ||--o{ OrderLine : "contains"
    Order ||--o{ PaymentIntent : "may have"
    PaymentIntent ||--o{ PaymentAttempt : "records"
    Order ||--o{ Refund : "issues"
    PaymentIntent ||--o{ Refund : "against"
    Order ||--o{ LedgerEntry : "produces"
    PaymentIntent ||--o{ LedgerEntry : "produces"
    InventoryItem ||--o{ StockReservation : "reserves"
    Order ||--o{ StockReservation : "owns"
    ReconciliationRun ||--o{ ReconciliationDiscrepancy : "reports"

    Product {
      Guid Id PK
      string Sku UK
      string Name
      decimal Price_Amount
      string Price_Currency
      bool IsActive
      long Version
    }
    InventoryItem {
      Guid Id PK
      Guid ProductId FK,UK
      string Sku
      long OnHand
      long Version
    }
    StockReservation {
      Guid Id PK
      Guid InventoryItemId FK
      Guid OrderId
      long Quantity
      int State
      timestamptz CreatedAtUtc
    }
    Order {
      Guid Id PK
      string CustomerRef
      string Currency
      int Status
      Guid PaymentIntentId "nullable"
      decimal RefundedTotal_Amount
      timestamptz CreatedAtUtc
      timestamptz UpdatedAtUtc
      long Version
    }
    OrderLine {
      Guid Id PK
      Guid OrderId FK
      Guid ProductId
      string Sku
      long Quantity
      decimal UnitPrice_Amount
      string UnitPrice_Currency
      Guid ReservationId "nullable"
    }
    PaymentIntent {
      Guid Id PK
      Guid OrderId FK
      decimal Amount
      string Currency
      decimal CapturedAmount
      string CapturedCurrency
      int Status
      string IdempotencyKey UK
      string ProviderReference
      timestamptz CreatedAtUtc
      timestamptz UpdatedAtUtc
      long Version
    }
    PaymentAttempt {
      Guid Id PK
      Guid PaymentIntentId FK
      int AttemptNumber
      string Outcome
      string ProviderReference "nullable"
      timestamptz AttemptedAtUtc
      int LatencyMs
    }
    Refund {
      Guid Id PK
      Guid OrderId FK
      Guid PaymentIntentId FK
      decimal Amount
      string Currency
      string Reason
      timestamptz IssuedAtUtc
      long Version
    }
    LedgerEntry {
      Guid Id PK
      Guid OrderId FK
      Guid PaymentIntentId "nullable FK"
      Guid RefundId "nullable FK"
      int Kind
      long AmountMinor
      string Currency
      string Source
      timestamptz OccurredAtUtc
    }
    IdempotencyRecord {
      string Key
      string Endpoint
      string RequestHash
      int ResponseStatus
      string ResponseContentType
      string ResponseBody
      string CorrelationId
      timestamptz CreatedAtUtc
    }
    OutboxMessage {
      Guid Id PK
      string Topic
      string PayloadJson
      timestamptz OccurredAtUtc
      timestamptz NextAttemptAtUtc
      int Attempts
      bool Dispatched
      timestamptz DispatchedAtUtc "nullable"
      string LastError "nullable"
      string CorrelationId
    }
    OutboxDeadLetter {
      Guid Id PK
      Guid OriginalMessageId
      string Topic
      string PayloadJson
      string LastError
      int Attempts
      timestamptz DeadLetteredAtUtc
      string CorrelationId
    }
    WebhookReplayRecord {
      string SignatureHash PK
      timestamptz SeenAtUtc
    }
    AuditEvent {
      Guid Id PK
      string Actor
      string Action
      string Resource
      string CorrelationId
      string BeforeHash
      string AfterHash
      timestamptz OccurredAtUtc
    }
    ReconciliationRun {
      Guid Id PK
      timestamptz StartedAtUtc
      timestamptz CompletedAtUtc "nullable"
      string SourceFileName
      int TotalProviderRows
      int TotalInternalRows
      int MatchedCount
      int MissingInProviderCount
      int MissingInternallyCount
      int AmountMismatchCount
      int DuplicateCount
      int StatusMismatchCount
    }
    ReconciliationDiscrepancy {
      Guid Id PK
      Guid RunId FK
      int Kind
      string PaymentIntentId "nullable"
      string ProviderReference "nullable"
      long InternalMinorUnits "nullable"
      long ProviderMinorUnits "nullable"
      string Currency
      string InternalStatus "nullable"
      string ProviderStatus "nullable"
      string Notes
    }
```

## Indexes

| Table | Index | Reason |
|---|---|---|
| `Products` | UNIQUE `(Sku)` | SKU lookup + duplicate protection. |
| `InventoryItems` | UNIQUE `(ProductId)` | One row per product. |
| `StockReservations` | `(InventoryItemId, State)` | Fast "available" calc. |
| `Orders` | `(Status)`, `(CustomerRef)` | Status filtering + customer lookup. |
| `OrderLines` | `(OrderId)` | Include navigation. |
| `PaymentIntents` | UNIQUE `(OrderId, IdempotencyKey)` | Second `authorize` with same key must find the prior intent. |
| `PaymentIntents` | `(Status)` | Reconciliation queries. |
| `PaymentAttempts` | `(PaymentIntentId, AttemptNumber)` | Ordered replay. |
| `Refunds` | `(OrderId)`, `(PaymentIntentId)` | Look up refunds by order or intent. |
| `LedgerEntries` | `(OrderId)`, `(PaymentIntentId)` | Journal queries. |
| `IdempotencyRecords` | UNIQUE `(Key, Endpoint)` | The core idempotency invariant. |
| `OutboxMessages` | `(Dispatched, NextAttemptAtUtc)` | Dispatcher poll query. |
| `OutboxDeadLetters` | `(OriginalMessageId)` | DLQ re-drive lookup. |
| `WebhookReplayRecords` | PK on `SignatureHash` | Replay guard. |
| `AuditEvents` | `(Resource)`, `(CorrelationId)` | Trace + audit trail queries. |
| `ReconciliationRuns` | `(StartedAtUtc DESC)` | Recent runs. |
| `ReconciliationDiscrepancies` | `(RunId, Kind)` | Filter discrepancies by kind. |

## Concurrency tokens

All aggregates (`Order`, `InventoryItem`, `PaymentIntent`, `Refund`, `Product`)
carry a `Version : long` property configured as `IsConcurrencyToken()`. Any
concurrent modification will fail SaveChanges with a
`DbUpdateConcurrencyException`, which application services translate to a
`409 Conflict` ProblemDetails and the client can retry.

## Global EF conventions

Applied in `AppDbContext.OnModelCreating`:

- Every `Guid` primary key is `ValueGeneratedNever()` — EF trusts the
  domain's `IIdGenerator`-assigned value on `INSERT`.
- Every `DateTimeOffset` column uses `DateTimeOffsetToBinaryConverter` so
  `WHERE NextAttemptAtUtc <= now` translates on SQLite.
- Navigation collections `Order.Lines` and `InventoryItem.Reservations` are
  `AutoInclude()` so services never need to remember `Include()` for
  correctness-critical calculations like `Order.Total` or `InventoryItem.Available`.
