# Database Schema

SQLite is the default store. EF Core creates the schema with `EnsureCreated` for this portable
demonstration. Production adoption should introduce reviewed migrations and a database that
supports multi-writer concurrency controls.

```mermaid
erDiagram
    Secrets ||--o{ SecretVersions : owns
    Secrets ||--o{ SecretTags : classified_by
    Secrets ||--o{ SecretConsumers : consumed_by
    Consumers ||--o{ SecretConsumers : subscribes
    Secrets ||--o{ Rotations : rotates
    Rotations ||--o{ ConsumerAcknowledgements : waits_for
    Consumers ||--o{ ConsumerAcknowledgements : acknowledges
    Secrets ||--o{ AuditRecords : audited

    Secrets {
      uuid Id PK
      string Name UK
      string Type
      string OwnerTeam
      string Environment
      string Criticality
      timespan RotationInterval
      timespan MaxAge
      timespan GracePeriod
      datetime CreatedAt
      long ConcurrencyVersion
    }
    SecretVersions {
      uuid Id PK
      uuid SecretId FK
      int VersionNumber
      string State
      blob Ciphertext
      blob Nonce
      blob AuthenticationTag
      blob WrappedDataEncryptionKey
      string KeyVersion
      datetime CreatedAt
      datetime ActivatedAt
      datetime ExpiresAt
      datetime DestroyedAt
    }
    Rotations {
      uuid Id PK
      uuid SecretId FK
      string IdempotencyKey UK
      string Strategy
      string State
      int PreviousVersionNumber
      int NewVersionNumber
      datetime AcknowledgementDeadline
      datetime MaintenanceWindowStart
    }
    ConsumerAcknowledgements {
      uuid RotationId PK,FK
      uuid ConsumerId PK,FK
      string Status
      datetime NotificationSentAt
      datetime AcknowledgedAt
    }
    AccessPolicies {
      uuid Id PK
      string Subject
      string PathPattern
      bool CanManageMetadata
      bool CanReadValues
      bool CanOperateRotations
      bool CanBreakGlass
    }
    AuditRecords {
      uuid Id PK
      uuid SecretId FK
      string Actor
      string Action
      string Resource
      string Reason
      string CorrelationId
      datetime OccurredAt
      string Outcome
    }
    ApprovalRequests {
      uuid Id PK
      string Operation
      string Resource
      string RequestedBy
      string ApprovedBy
      datetime ExpiresAt
      datetime ExecutedAt
    }
```

## Constraints and indexes

| Table | Constraint/index | Purpose |
|---|---|---|
| `Secrets` | unique `Name` | One registry entry per hierarchical path |
| `SecretVersions` | unique `(SecretId, VersionNumber)` | Ordered history cannot fork |
| `SecretTags` | primary `(SecretId, Value)` | Deduplicated classification |
| `SecretConsumers` | primary `(SecretId, ConsumerId)` | Deduplicated subscription |
| `Consumers` | unique `(Application, Name)` | Stable consumer identity |
| `Rotations` | unique `IdempotencyKey` | Safe request replay |
| `Rotations` | `(SecretId, State)` | Find active operations |
| `ConsumerAcknowledgements` | primary `(RotationId, ConsumerId)` | One acknowledgement per consumer |
| `AccessPolicies` | unique `(Subject, PathPattern)` | Avoid ambiguous duplicates |
| `AuditRecords` | `(SecretId, OccurredAt)`, `(Actor, OccurredAt)` | Access and anomaly reports |

## Sensitive columns

Only encrypted bytes and wrapped DEKs are persisted. Nonce and authentication tag are not secret
but are integrity-critical. Plaintext values, generated private keys, certificate export
passwords, JWTs, and master-key material are not database columns.

`Destroyed` versions retain lifecycle metadata while cryptographic byte arrays and key version are
cleared. Audit rows are append-only through the application interface.
