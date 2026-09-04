# Database Schema

The platform persists via **EF Core** to **SQLite** by default (`Database:ConnectionString`, default
`Data Source=idp-dev.db`). All entity primary keys are `Guid` and **domain-assigned**
(`ValueGeneratedNever()`), so identity comes from the domain, not the database. Money is stored as
`decimal`. Timestamps are UTC.

## Entity–relationship diagram

```mermaid
erDiagram
    Documents ||--o{ ExtractedFields : has
    Documents ||--o{ LineItems : has
    Documents ||--o{ PipelineTransitions : has
    Documents ||--o{ DocumentValidations : has
    Documents ||--o| ReviewTasks : "may have"
    Documents ||--o{ Corrections : "corrected by"
    Documents ||--o{ ExportRecords : "exported by"
    Documents }o--o| Suppliers : "assigned to"
    Suppliers ||--o{ SupplierHints : learns

    Documents {
        guid Id PK
        string FileName
        string ContentType
        string ContentHash "indexed"
        string StorageKey
        long SizeBytes
        int DocumentType "indexed"
        int State "indexed"
        int Routing "nullable"
        double ClassificationConfidence
        string ClassificationExplanation
        double DocumentConfidence
        decimal DocumentValue "nullable"
        string Currency "nullable"
        guid SupplierId FK "nullable"
        string SupplierNameRaw "nullable"
        int Version
        string CorrelationId
        datetime CreatedAtUtc
        datetime UpdatedAtUtc
    }
    ExtractedFields {
        guid Id PK
        guid DocumentId FK
        string FieldKey
        string RawValue
        string NormalizedValue
        double Confidence
        int Strategy
        bool IsRequired
        string SourceText
        double BoxX
        double BoxY
        double BoxWidth
        double BoxHeight
        int BoxPage
    }
    LineItems {
        guid Id PK
        guid DocumentId FK
        int LineNumber
        string Description
        decimal Quantity
        decimal UnitPrice
        decimal LineTotal
        decimal TaxRate
        double Confidence
    }
    DocumentValidations {
        guid Id PK
        guid DocumentId FK
        string RuleName
        int Outcome
        string Message
        string ImplicatedFields
    }
    PipelineTransitions {
        guid Id PK
        guid DocumentId FK
        int FromState
        int ToState
        string Reason
        string Actor
        datetime OccurredAtUtc
    }
    Suppliers {
        guid Id PK
        string Name "indexed"
        string TaxId
        string DefaultCurrency
        string Aliases
        string BankAccount
        bool IsActive
    }
    SupplierHints {
        guid Id PK
        guid SupplierId FK
        string FieldKey
        string AnchorText
        int TimesReinforced
    }
    ReviewTasks {
        guid Id PK
        guid DocumentId FK "unique"
        int Status
        double Priority
        string ClaimedBy "nullable"
        datetime ClaimExpiresUtc "nullable"
        datetime CreatedAtUtc
        datetime SlaDueUtc
    }
    Corrections {
        guid Id PK
        guid DocumentId FK
        string FieldKey
        string OldValue
        string NewValue
        string Reason
        string Reviewer
        datetime CreatedAtUtc
    }
    ExportRecords {
        guid Id PK
        guid DocumentId FK "indexed"
        string IdempotencyKey "unique"
        int Status "indexed"
        string Format
        int Attempts
        int MaxAttempts
        string ErpReference "nullable"
        string OutboxPath "nullable"
        string LastError "nullable"
        datetime CreatedAtUtc
        datetime UpdatedAtUtc
    }
    AuditEntries {
        guid Id PK
        string Actor
        string Action
        string Resource "indexed"
        string CorrelationId
        datetime TimestampUtc "indexed"
        string BeforeHash "nullable"
        string AfterHash "nullable"
        string Detail "nullable"
    }
```

## Tables, keys and indexes

| Table | PK | Foreign keys | Indexes |
| --- | --- | --- | --- |
| `Documents` | `Id` | `SupplierId → Suppliers.Id` (nullable) | `ContentHash`, `State`, `DocumentType` |
| `ExtractedFields` | `Id` | `DocumentId → Documents.Id` (cascade) | `(DocumentId, FieldKey)` |
| `LineItems` | `Id` | `DocumentId → Documents.Id` (cascade) | `DocumentId` |
| `PipelineTransitions` | `Id` | `DocumentId → Documents.Id` (cascade) | `DocumentId` |
| `DocumentValidations` | `Id` | `DocumentId → Documents.Id` (cascade) | `DocumentId` |
| `Suppliers` | `Id` | — | `Name` |
| `SupplierHints` | `Id` | `SupplierId → Suppliers.Id` (cascade) | `(SupplierId, FieldKey)` |
| `ReviewTasks` | `Id` | `DocumentId → Documents.Id` | **unique** `DocumentId`, `(Status, Priority)` |
| `Corrections` | `Id` | `DocumentId → Documents.Id` | `DocumentId` |
| `ExportRecords` | `Id` | `DocumentId → Documents.Id` | **unique** `IdempotencyKey`, `DocumentId`, `Status` |
| `AuditEntries` | `Id` | — | `TimestampUtc`, `Resource` |

## Notable design points

- **Owned bounding box.** `ExtractedField.Box` (`WordBox`) is persisted inline as
  `BoxX/BoxY/BoxWidth/BoxHeight/BoxPage` columns on `ExtractedFields`.
- **Aliases / implicated fields** are stored as delimited strings (`Suppliers.Aliases`,
  `DocumentValidations.ImplicatedFields`) rather than child tables — they are small, read-mostly value
  lists.
- **Enum columns** (`DocumentType`, `State`, `Routing`, `Strategy`, `Outcome`, `Status`) are stored as
  integers.
- **Uniqueness guarantees.** One `ReviewTask` per document (`unique DocumentId`) and one ERP booking
  per idempotency key (`unique IdempotencyKey`) — the latter is the database-level backstop for
  idempotent export.
- **Cascade deletes** from `Documents` to its child collections keep a document's data consistent when
  it is removed; `Suppliers → SupplierHints` cascades likewise.
- **Audit** rows are append-only by convention (no update/delete paths in application code) and carry
  before/after hashes for tamper-evidence.

## Migration / creation

For the SQLite default the schema is created from the model on startup (seeded in Development/Testing).
A production Postgres deployment would use EF Core migrations; the model is provider-agnostic (no
SQLite-only column types are used).
