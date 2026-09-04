# Database schema

SQLite by default (via `Data Source=zero-trust.db`), Postgres or SQL Server work
identically via configuration. Schema is created by `EnsureCreatedAsync` at startup
(migrations are not the teaching point of this project).

## ER diagram

```mermaid
erDiagram
    users ||--o{ refresh_tokens : "issues"
    users ||--o{ revoked_tokens : "may revoke"
    partners ||--o{ refresh_tokens : "client_credentials"
    partners ||--o{ api_keys : "owns legacy"
    partners ||--o{ payment_initiation_requests : "initiates"
    users ||--o{ accounts : "owns"
    accounts ||--o{ statements : "has"
    signing_keys ||--o{ audit_records : "not related (info)"
    users ||--o{ audit_records : "actor"
    partners ||--o{ audit_records : "actor"
    users ||--o{ break_glass_grants : "requester/approver"

    users {
        guid Id PK
        string Subject "UNIQUE"
        string Email "UNIQUE"
        string DisplayName
        string PasswordHash "PBKDF2 100k SHA256"
        string PasswordSalt
        string Roles "comma-separated"
        bool MfaEnrolled
        datetime CreatedAtUtc
    }

    partners {
        guid Id PK
        string PartnerCode "UNIQUE"
        string DisplayName
        string ClientId "UNIQUE"
        string ClientSecretHash
        string ClientSecretSalt
        string AllowedScopes
        string AllowedIps
        string ClientCertThumbprint
        int RateLimitPermitsPerMinute
        bool Enabled
        datetime CreatedAtUtc
    }

    refresh_tokens {
        guid Id PK
        string TokenHash "UNIQUE"
        guid FamilyId "INDEX"
        string Subject
        string Audience
        string Scopes
        datetime ExpiresAtUtc
        datetime ConsumedAtUtc "nullable"
        string RevokedReason "nullable"
        datetime CreatedAtUtc
    }

    revoked_tokens {
        guid Id PK
        string Jti "UNIQUE"
        string Subject
        string Reason
        datetime CreatedAtUtc
    }

    api_keys {
        guid Id PK
        string KeyId "UNIQUE"
        string KeyHash
        string KeySalt
        string OwnerPartnerCode
        string AllowedScopes
        datetime DeprecatedAfterUtc "nullable"
        bool Revoked
        datetime LastUsedAtUtc "nullable"
        int UsageCount
        datetime CreatedAtUtc
    }

    signing_keys {
        guid Id PK
        string Kid "UNIQUE"
        string Algorithm "RS256|HS256"
        string PublicKeyPem
        string PrivateKeyPem
        bool IsPrimary
        datetime NotBeforeUtc
        datetime NotAfterUtc "nullable"
        bool Retired
        datetime CreatedAtUtc
    }

    audit_records {
        guid Id PK
        long Sequence "monotonic; UNIQUE INDEX"
        int Kind
        string Actor
        string Action
        string Resource
        string CorrelationId
        string SourceIp
        string UserAgent
        string Detail
        bool Allowed
        string PreviousHash
        string Hash
        datetime CreatedAtUtc
    }

    break_glass_grants {
        guid Id PK
        string RequesterSubject
        string ApproverSubject
        string Justification "min 10 chars"
        datetime WindowStartUtc
        datetime WindowEndUtc
        datetime UsedAtUtc "nullable"
        datetime CreatedAtUtc
    }

    accounts {
        guid Id PK
        string AccountNumber "UNIQUE"
        string OwnerSubject
        string Nickname
        string Currency
        decimal BalanceMinorUnits
        datetime CreatedAtUtc
    }

    statements {
        guid Id PK
        guid AccountId
        string OwnerSubject
        int Year
        int Month
        decimal OpeningBalanceMinorUnits
        decimal ClosingBalanceMinorUnits
        string Currency
        datetime CreatedAtUtc
    }

    payment_initiation_requests {
        guid Id PK
        string ExternalReference "INDEX"
        string DebtorAccountNumber
        string CreditorAccountNumber
        decimal AmountMinorUnits
        string Currency
        string InitiatedByPartner
        string Status
        string IdempotencyKey "UNIQUE"
        datetime CreatedAtUtc
    }
```

## Indexes and constraints

| Table                       | Constraint                                         |
|-----------------------------|-----------------------------------------------------|
| users                       | UNIQUE(Subject), UNIQUE(Email)                     |
| partners                    | UNIQUE(PartnerCode), UNIQUE(ClientId)              |
| refresh_tokens              | UNIQUE(TokenHash), INDEX(FamilyId)                 |
| revoked_tokens              | UNIQUE(Jti)                                        |
| api_keys                    | UNIQUE(KeyId)                                      |
| signing_keys                | UNIQUE(Kid)                                        |
| audit_records               | UNIQUE(Sequence)                                   |
| accounts                    | UNIQUE(AccountNumber)                              |
| payment_initiation_requests | INDEX(ExternalReference), UNIQUE(IdempotencyKey)   |

## Rationale

- **PBKDF2 hashes stored, salts stored separately.** Salted PBKDF2 with 100 000 iterations
  is safe against offline dictionary attacks on the hosts this project targets. Argon2 is
  strictly better; PBKDF2 is what ships with .NET out of the box, which matters for the
  "zero external dependencies" claim.
- **Refresh tokens stored as hashes, plaintext returned only on issuance.** A DB dump does
  not leak live refresh tokens.
- **Refresh `FamilyId` index.** Reuse detection means "revoke every row in a family";
  cheap only if indexed.
- **Audit `Sequence` unique.** Monotonic sequence + hash chain gives cheap detection of
  insertion / reordering / modification.
- **Money is `decimal` in minor units.** No `double` money. See architecture standards §4.
- **Payment idempotency key unique.** Duplicate submission returns the original result.
