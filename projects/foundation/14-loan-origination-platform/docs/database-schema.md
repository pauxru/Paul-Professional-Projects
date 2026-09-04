# Database Schema

SQLite is the default local store. EF Core maps explicit keys and indexes for lookup paths; aggregate payloads are JSON snapshots to preserve immutable historical decision context.

```mermaid
erDiagram
  CUSTOMERS ||--o{ APPLICATIONS : applies_for
  PRODUCTS ||--o{ APPLICATIONS : bound_version
  RULESETS ||--o{ APPLICATIONS : bound_version
  APPLICATIONS ||--o| UNDERWRITING_QUEUE : queues
  APPLICATIONS ||--o{ OFFERS : receives
  APPLICATIONS ||--o| DECISION_RECORDS : explains
  APPLICATIONS ||--o{ DISBURSEMENTS : funds
  APPLICATIONS ||--o{ AUDITS : affects
  CUSTOMERS {
    uuid id PK
    string legal_name
    string deduplication_key UK
    integer created_at_unix_ms
    text payload_json
  }
  PRODUCTS {
    uuid id PK
    string product_code
    integer version
    integer effective_from_unix_ms
    text payload_json
  }
  RULESETS {
    string ruleset_id PK
    integer version PK
    integer effective_from_unix_ms
    text payload_json
  }
  APPLICATIONS {
    uuid id PK
    uuid customer_id FK
    string stage
    integer version
    integer created_at_unix_ms
    text payload_json
  }
  UNDERWRITING_QUEUE {
    uuid application_id PK
    decimal exposure
    integer priority
    integer queued_at_unix_ms
    integer sla_due_at_unix_ms
    text payload_json
  }
  OFFERS {
    uuid id PK
    uuid application_id FK
    integer version
    string status
    text payload_json
  }
  DISBURSEMENTS {
    uuid id PK
    string provider_reference UK
    string status
    text payload_json
  }
  AUDITS {
    uuid id PK
    integer occurred_at_unix_ms
    string correlation_id
    text payload_json
  }
  DECISION_RECORDS {
    uuid id PK
    uuid application_id UK
    integer created_at_unix_ms
    text payload_json
  }
```

## Tables, keys, and indexes

| Table | Primary key | Unique/indexed lookup paths | Purpose |
|---|---|---|---|
| `Customers` | `Id` | unique `DeduplicationKey`, `LegalName` index | Synthetic applicant profile snapshot |
| `Products` | `Id` | unique `(ProductCode, Version)` | Immutable product versions |
| `Rulesets` | `(RulesetId, Version)` | composite primary key | Immutable declarative ruleset versions |
| `Applications` | `Id` | `CustomerId`; `(Stage, CreatedAtUnixMilliseconds)` | Workflow aggregate and optimistic `Version` |
| `UnderwritingQueue` | `ApplicationId` | `(Priority, SlaDueAtUnixMilliseconds)` | Claim/lock and triage projection; payload retains exposure and queue age |
| `Offers` | `Id` | unique `(ApplicationId, Version)` | Versioned terms, schedule, acceptance state |
| `Disbursements` | `Id` | unique `ProviderReference` | Provider idempotency and reconciliation |
| `Audits` | `Id` | occurrence timestamp, correlation ID | Append-only audit hash chain |
| `DecisionRecords` | `Id` | unique `ApplicationId` | Historical explanation bundle |

## Snapshot contents
`Applications.Payload` contains applicant facts, stage events, document metadata, bound product/ruleset versions, KYC status, decision trace, risk assessment, and underwriting decision. Document bytes are not stored in SQLite; `LoanDocument.ObjectKey` points to the configured local object-store path. `DecisionRecords.Payload` repeats the immutable facts, trace, product/ruleset provenance, and scorecard version intentionally for regulatory-style reconstruction.

## Concurrency and integrity
Application saves compare the stored `Version` to the caller’s expected version and reject stale updates. Product/ruleset version uniqueness prevents replacement. Provider reference uniqueness gives idempotent disbursement semantics. The application never exposes a mutation/deletion API for audit rows; each `AfterHash` chains from the previous row hash.
