# Database schema

The service uses SQLite by default (see ADR 0003). All types are shown in
SQLite terms; `DateTimeOffset` is persisted as a 64-bit integer via
`DateTimeOffsetToBinaryConverter` (globally configured in `RagDbContext`).

## ER diagram

```mermaid
erDiagram
    DOCUMENT ||--o{ DOCUMENT_CHUNK : "chunks"
    DOCUMENT ||--|| ACL : "owns"
    CHAT_SESSION ||--o{ CHAT_MESSAGE : "messages"
    PROMPT_TEMPLATE ||--o{ CHAT_MESSAGE : "used in"
    FEEDBACK }o--|| PROMPT_TEMPLATE : "prompt version"

    DOCUMENT {
        Guid Id PK
        string Title
        string Source
        string Content
        string ContentHash "SHA256, unique"
        int Version "concurrency token"
        int acl_classification
        string acl_roles "pipe-joined"
        string acl_departments "pipe-joined"
        int64 CreatedAt
        int64 UpdatedAt
    }
    DOCUMENT_CHUNK {
        Guid Id PK
        Guid DocumentId FK
        int Sequence
        int StartChar
        int EndChar
        string Content
        blob Embedding "float[] blob"
        string EmbeddingModelId
        int EmbeddingDimensions
    }
    ACL {
        int Classification
        string Roles
        string Departments
    }
    CHAT_SESSION {
        Guid Id PK
        string UserId
        string Title
        int64 CreatedAt
        int64 UpdatedAt
    }
    CHAT_MESSAGE {
        Guid Id PK
        Guid SessionId FK
        int Role
        string Content
        int64 Timestamp
        string PromptVersion
    }
    PROMPT_TEMPLATE {
        Guid Id PK
        string Name
        string Version
        string Body
        string Hash "SHA256"
        bool IsActive
        int64 CreatedAt
    }
    FEEDBACK {
        Guid Id PK
        string UserId
        string Query
        string Answer
        int Rating
        string Reason
        string PromptVersion
        string CitedChunkIds "JSON string list"
        int64 CreatedAt
    }
    USAGE_ENTRY {
        int64 Id PK
        string Tenant
        string UserId
        string Model
        int PromptTokens
        int CompletionTokens
        decimal Cost
        string PromptVersion
        int64 Timestamp
    }
```

## Table notes

- **Documents.**
  `ContentHash` is unique — this is what makes ingestion idempotent
  (`IngestionService` reuses the existing document when the hash matches).
  `Version` is the EF Core concurrency token; a re-ingest bumps it and
  clears the chunk list before re-adding.
  `Acl` is owned (`OwnsOne`) — the columns are inlined onto `documents`
  as `acl_classification`, `acl_roles`, `acl_departments`. Roles /
  departments are stored pipe-joined via a value converter.

- **DocumentChunk.** Embeddings are stored as raw `float[]` bytes via
  `Buffer.BlockCopy`. Decoding is done in-memory at `SqliteVectorStore`
  boot. `Sequence`/`StartChar`/`EndChar` allow citation spans to point at
  the exact substring in `Document.Content`.

- **ChatSession/ChatMessage.** Cascade delete on session id. Messages are
  inserted individually by `ChatSessionRepository.AppendMessagesAsync`
  using `ExecuteUpdateAsync` on the parent session to bump `UpdatedAt`.

- **PromptTemplate.** `Hash = SHA-256(Body)`. Registering the same
  `(Name, Version)` with a different body returns `409 Conflict`; a new
  `Version` deactivates all previous ones for that name.

- **Feedback.** `CitedChunkIds` is serialised JSON — feedback rows are
  written rarely and read for offline analysis, so JSON is fine.

- **UsageEntry.** Feeds the budget guard. Aggregated per tenant per day.

## Migrations

The project uses `EnsureCreatedAsync` at boot for the dev/CI experience —
there are no EF migrations yet. Adding them is straightforward
(`dotnet ef migrations add InitialCreate`), and required before running in
production against a real database.
