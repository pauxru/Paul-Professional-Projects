# Database Schema

SQLite is the default database. EF Core creates the schema with `EnsureCreated` because migration mechanics are not the teaching focus of this case study.

```mermaid
erDiagram
    FLOWS ||--o{ FLOW_VERSIONS : has
    RUNS ||--o{ RUN_STEPS : records
    RUNS ||--o{ DEAD_LETTERS : produces
    RUNS ||--o{ CHECKPOINTS : advances
    FLOWS ||--o{ RUNS : executes

    FLOWS {
      uuid Id PK
      string Name
      int ActiveVersion
    }
    FLOW_VERSIONS {
      uuid FlowId PK,FK
      int Version PK
      string Format
      text Definition
      datetime CreatedAt
      string CreatedBy
    }
    RUNS {
      uuid Id PK
      uuid FlowId
      int FlowVersion
      int Status
      string CorrelationId
      datetime StartedAt
      datetime CompletedAt
      int RecordsProcessed
      text Error
    }
    RUN_STEPS {
      uuid Id PK
      uuid RunId FK
      string StepId
      int Kind
      datetime StartedAt
      datetime CompletedAt
      text InputSnapshot
      text OutputSnapshot
      int RecordsIn
      int RecordsOut
      text Error
    }
    DEAD_LETTERS {
      uuid Id PK
      uuid RunId
      string StepId
      string BatchKey
      string RecordKey
      text Payload
      text Error
      int Status
      datetime CreatedAt
      uuid ReplayRunId
    }
    CHECKPOINTS {
      uuid RunId PK
      string StepId PK
      string BatchKey PK
      int NextIndex
    }
    IDEMPOTENCY_KEYS {
      string Scope PK
      string Key PK
      int StatusCode
      text Payload
      datetime CreatedAt
    }
    CONTRACT_DRIFT_ALERTS {
      uuid Id PK
      string ConnectorId
      string Operation
      string FieldPath
      string Change
      datetime DetectedAt
      bool Resolved
    }
    WEBHOOK_NONCES {
      string Nonce PK
      datetime ExpiresAt
    }
```

## Keys and indexes

- `FlowVersions`: composite primary key `(FlowId, Version)`.
- `Runs`: indexes on `(FlowId, StartedAt)`, `(Status, StartedAt)`, and `CorrelationId`.
- `RunSteps`: index on `(RunId, StartedAt)`.
- `DeadLetters`: indexes on `(RunId, BatchKey)` and `(Status, CreatedAt)`; unique `(RunId, StepId, RecordKey)` prevents duplicate quarantine within a run.
- `Checkpoints`: composite primary key `(RunId, StepId, BatchKey)`.
- `IdempotencyKeys`: composite primary key `(Scope, Key)` and retention index on `CreatedAt`.
- `ContractDriftAlerts`: operational indexes by resolution/date and connector/operation/path.
- `WebhookNonces`: primary-key uniqueness provides atomic replay protection; `ExpiresAt` supports cleanup.

## Data handling

Run snapshots are redacted before persistence. Secrets are never stored in this schema; the local adapter writes a separate AES-GCM envelope. Production should additionally encrypt database storage, restrict database principals, and apply retention/erasure policies appropriate to the organization.
