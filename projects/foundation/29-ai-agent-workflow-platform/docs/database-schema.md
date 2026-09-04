# Database Schema

The platform uses a single EF Core `DbContext` (`AgentDbContext`). **SQLite is the default and only
required provider** (`Data Source=agentplatform.db`; an in-memory SQLite connection is used for
tests). The schema is created at startup with `EnsureCreated()` and seeded idempotently by
`DatabaseSeeder`. The same model runs on Postgres if you wire Npgsql, but SQLite is the default.

Two groups of tables:

1. **Agent aggregates** — the durable orchestration state: runs, per-step executions, trace events,
   approvals and idempotency records. These are what make a run *resumable* and *replayable*.
2. **Business data** — the fictional records the tools operate on: customers, tickets, knowledge
   articles, orders, plus the durable outputs (refund requests, an email outbox, an audit log).

> **SQLite `DateTimeOffset` note.** SQLite cannot `ORDER BY` or range-compare a `DateTimeOffset`
> stored as text. A global value converter persists every `DateTimeOffset` as a `long` of UTC ticks
> (INTEGER), so ordering and time filters are correct on every provider. All timestamps are UTC.

---

## ER diagram

```mermaid
erDiagram
    RUNS ||--o{ STEP_EXECUTIONS : "has"
    RUNS ||--o{ TRACE_EVENTS : "records"
    RUNS ||--o{ APPROVALS : "may pause for"
    RUNS ||--o{ IDEMPOTENCY_RECORDS : "guards"
    RUNS ||--o{ REFUND_REQUESTS : "may produce"
    RUNS ||--o{ EMAIL_OUTBOX : "may produce"
    RUNS ||--o{ AUDIT_LOG : "writes"
    CUSTOMERS ||--o{ TICKETS : "raises"
    CUSTOMERS ||--o{ ORDERS : "places"
    CUSTOMERS ||--o{ REFUND_REQUESTS : "receives"

    RUNS {
        string Id PK
        string WorkflowName
        int WorkflowVersion
        string TenantId
        string Status
        string Outcome
        string HaltReason
        string CurrentStepId
        string StateJson
        string BudgetJson
        int TokensUsed
        decimal CostUsed
        int ToolCalls
        int ModelCalls
        string IdempotencyKey
        int Version "concurrency token"
        long CreatedAt "UTC ticks"
    }
    STEP_EXECUTIONS {
        string Id PK
        string RunId FK
        string StepId
        string Kind
        int Attempt
        int Ordinal
        string Status
        string InputJson
        string OutputJson
        string ErrorCode
        long DurationMs
    }
    TRACE_EVENTS {
        string Id PK
        string RunId FK
        int Ordinal
        string Type
        string StepId
        string ToolName
        bool Success
        int PromptTokens
        int CompletionTokens
        decimal Cost
        string PromptVersion
        string DataJson
    }
    APPROVALS {
        string Id PK
        string RunId FK
        string StepId
        string TenantId
        string Status
        string RiskLevel
        string ProposedActionJson
        string ReasoningTrace
        bool WasModified
        string ModifiedArgumentsJson
        string DecidedBy
        long RequestedAt "UTC ticks"
        long ExpiresAt "UTC ticks"
    }
    IDEMPOTENCY_RECORDS {
        string Key PK
        string RunId
        string ToolName
        string ResultJson
        long CreatedAt "UTC ticks"
    }
    CUSTOMERS {
        string Id PK
        string Name
        string Email
        string Tier
        decimal LifetimeValueUsd
        int PriorRefundCount
    }
    TICKETS {
        string Id PK
        string CustomerId FK
        string Subject
        string Body
        string Category
        string Priority
        string Status
    }
    KNOWLEDGE_ARTICLES {
        string Id PK
        string Title
        string Body
        string Category
        string Tags
    }
    ORDERS {
        string Id PK
        string CustomerId FK
        decimal AmountUsd
        long PurchasedAt "UTC ticks"
        bool ItemReturned
        string Reason
    }
    REFUND_REQUESTS {
        string Id PK
        string CustomerId FK
        string OrderId
        decimal AmountUsd
        string Reason
        string Status
        string RunId
    }
    EMAIL_OUTBOX {
        string Id PK
        string To
        string Subject
        string Body
        string RunId
    }
    AUDIT_LOG {
        string Id PK
        string RunId
        string Actor
        string Action
        string DetailsJson
    }
```

---

## Tables, keys and indexes

### Agent aggregates

| Table | PK | Indexes | Notes |
|---|---|---|---|
| `Runs` | `Id` | `(TenantId, IdempotencyKey)`, `Status` | `Version` is an optimistic-concurrency token. Idempotent run creation keys off `(TenantId, IdempotencyKey)`. `StateJson`/`BudgetJson` are the persisted run state + budget snapshot restored on resume. |
| `StepExecutions` | `Id` | `RunId` | FK → `Runs.Id` (cascade delete). One row per *attempt* of a step. `Ordinal` gives deterministic ordering; the engine skips steps that already have a `Completed` execution — this is the resume mechanism. |
| `TraceEvents` | `Id` | `(RunId, Ordinal)` | Append-only, immutable. Ordered by `Ordinal` they form the replayable trace. Carries per-event tokens/cost/latency and the exact `PromptVersion`. |
| `Approvals` | `Id` | `(TenantId, Status)`, `RunId` | Captures the full proposed action, arguments and reasoning trace. Audited: `DecidedBy`, `DecidedAt`, `DecisionNotes`. |
| `IdempotencyRecords` | `Key` | (PK) | `Key = {RunId}:{StepId}:{tool}:{canonical-args}`. Stores the tool result so a retry replays it instead of re-executing a side effect (at-most-once for mutating tools). |

### Business data (fictional)

| Table | PK | Indexes | Notes |
|---|---|---|---|
| `Customers` | `Id` | `Email` | `PriorRefundCount` feeds the deterministic fraud guard. |
| `Tickets` | `Id` | `CustomerId` | Includes seeded adversarial tickets (`TCK-INJ-1`, `TCK-UNAUTH`, `TCK-LOOP`, …). |
| `KnowledgeArticles` | `Id` | — | Returned by `search_knowledge_base`. |
| `Orders` | `Id` | `CustomerId` | Backs refund eligibility. |
| `RefundRequests` | `Id` | `CustomerId` | Durable output of the mutating `create_refund_request` tool; `RunId` links back to the run. |
| `EmailOutbox` | `Id` | — | `send_email` writes here — no real SMTP is ever contacted. |
| `AuditLog` | `Id` | `RunId` | Tamper-evident record of approvals and mutating actions. |

---

## Seed data (idempotent)

`DatabaseSeeder.SeedAsync` inserts a fixed, fictional dataset only if the tables are empty:

- Customers `CUST-001..` — e.g. `CUST-001` (0 prior refunds, eligible) and `CUST-003`
  (3 prior refunds → tripped by the fraud guard).
- Tickets `TCK-1001..TCK-1015` (auto-resolve category), `TCK-1016..TCK-1024` (escalate category),
  and adversarial tickets `TCK-INJ-1/2`, `TCK-UNAUTH`, `TCK-LOOP`, `TCK-OVERSIZE`, `TCK-MALFORMED`,
  `TCK-REFUSE`, `TCK-HALLUC`.
- Knowledge-base articles and orders backing the three workflows.

All demo data is fictional and labelled as such (e.g. `Contoso Retail (fictional)`).
