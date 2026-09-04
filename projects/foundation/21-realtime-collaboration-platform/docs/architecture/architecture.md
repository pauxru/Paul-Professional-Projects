# Architecture

Collab is a **modular monolith** in four layers with strict inward-pointing dependencies
(`Api → Infrastructure → Application → Domain`). The Domain has **no** framework dependencies; the
CRDT, diff, and conflict logic are pure and unit-testable in isolation.

## Layers

| Layer | Project | Responsibility | Depends on |
|---|---|---|---|
| Domain | `Collab.Domain` | RGA CRDT, structured LWW, diff engine, entities, role policy, `IClock` | (nothing) |
| Application | `Collab.Application` | Use-case services, the `CollaborationService` engine, contracts/DTOs, ports (`IUnitOfWork`, repositories, `IClientNotifier`, `ICollabMetrics`, `IPresenceStore`) | Domain |
| Infrastructure | `Collab.Infrastructure` | EF Core (`AppDbContext`), repositories, presence store, runtime cache, metrics, DB initializer/seed | Application |
| Api | `Collab.Api` | Minimal-API REST endpoints, SignalR hub, JWT auth, rate limiting, OpenTelemetry, static web client | Infrastructure |

The **Application** layer owns the realtime *semantics* (ordering, validation, persistence, resync)
behind ports; the **Api** layer owns the *transport* (SignalR hub) and calls into it. This keeps the
engine testable without a hub and lets the hub stay thin.

## Realtime architecture (container view)

```mermaid
flowchart TB
    subgraph Browser["Browser tab (web client)"]
        UI["index.html + textarea"]
        RGAjs["rga.js (RGA port)"]
        HUBjs["hub-client.js (hand-rolled SignalR JSON protocol)"]
        UI --> RGAjs --> HUBjs
    end

    subgraph API["Collab.Api (.NET 10, port 5021)"]
        Hub["CollaborationHub /hubs/collaboration"]
        REST["REST /api/v1/* (workspaces, documents, comments, notifications)"]
        Auth["JWT bearer / access_token query"]
        RL["HubRateLimiter (token bucket per connection)"]
        Flusher["PresenceFlusherService (coalesced broadcast)"]
        OTel["OpenTelemetry meter: clients, ops/sec, apply duration, resync, rejected"]
    end

    subgraph APP["Collab.Application"]
        Engine["CollaborationService (authoritative ordering + persistence + resync)"]
        Notifier["IClientNotifier -> SignalRClientNotifier"]
    end

    subgraph INFRA["Collab.Infrastructure"]
        Presence["IPresenceStore (in-memory)"]
        Repos["EF Core repositories"]
    end

    DB[("SQLite collab.db\nusers, workspaces, documents,\noperation_log, snapshots, comments,\nnotifications, audit_records")]

    HUBjs -- "WebSocket (JSON hub protocol)" --> Hub
    UI -- "POST /auth/token, REST" --> REST
    Hub --> RL --> Engine
    Hub --> Presence
    Flusher --> Presence
    Flusher -- "PresenceChanged" --> Hub
    Engine --> Repos --> DB
    Engine --> Notifier --> Hub
    REST --> Engine
    Auth -.-> Hub
    Auth -.-> REST
    Engine --> OTel
```

## Operation flow with transform/merge (sequence)

Two clients editing the same text document. The server is authoritative for sequence and
persistence; remote ops **merge** into each client's local RGA (no OT-style transform needed).

```mermaid
sequenceDiagram
    autonumber
    participant A as Client A (Ada, Editor)
    participant S as Server (Hub + CollaborationService)
    participant B as Client B (Grace, Editor)

    Note over A: types "x" locally (optimistic)
    A->>A: apply insert to local RGA (id 7@ada)
    A->>S: SubmitOperation({textOps:[insert 7@ada after Root]})
    S->>S: rate-limit check (token bucket)
    S->>S: validate causal readiness (parent exists?)
    S->>S: assign ServerSequence, append operation_log, maybe snapshot
    S-->>A: ack(sequence=42)  %% remove from pending
    S->>B: OperationApplied({sequence:42, insert 7@ada})
    B->>B: clock.observe(7); merge insert into local RGA
    Note over B: concurrently types "y" (id 7@grace)
    B->>S: SubmitOperation({textOps:[insert 7@grace after Root]})
    S->>S: assign sequence=43, persist
    S-->>B: ack(sequence=43)
    S->>A: OperationApplied({sequence:43, insert 7@grace})
    A->>A: merge; siblings sorted by DESC id -> deterministic order
    Note over A,B: both render identical text (e.g. "yx") — strong eventual consistency
```

## Reconnect / resync (sequence)

A client that was offline for K operations catches up from a checkpoint + the operation-log tail.

```mermaid
sequenceDiagram
    autonumber
    participant C as Client (was offline)
    participant S as Server

    C->>S: (reconnect) JoinDocument(docId)
    C->>S: ResyncDocument(docId, fromSequence = lastSeenSeq)
    alt client far behind or has no/stale state
        S->>S: load latest snapshot + replay tail
        S-->>C: Resynced{ full=true, checkpoint(state,seq), operations=[] }
        C->>C: replace local RGA from checkpoint state
    else client only missed a few ops
        S->>S: read operation_log where seq > fromSequence
        S-->>C: Resynced{ full=false, operations=[...missed...], currentSequence }
        C->>C: apply missed ops in order (causal buffer handles gaps)
    end
    Note over C: local document == server document at currentSequence
```

## Presence lifecycle

```mermaid
stateDiagram-v2
    [*] --> Joined: JoinDocument
    Joined --> Active: UpdatePresence / Ping (heartbeat fresh)
    Active --> Active: cursor moves (coalesced by flusher, ~150ms)
    Active --> Idle: no heartbeat for PresenceIdleSeconds (20s)
    Idle --> Active: UpdatePresence / Ping
    Active --> Evicted: no heartbeat for PresenceEvictSeconds (60s)
    Idle --> Evicted: no heartbeat for PresenceEvictSeconds (60s)
    Active --> Left: LeaveDocument / disconnect
    Idle --> Left: LeaveDocument / disconnect
    Evicted --> [*]
    Left --> [*]
    note right of Active
        Presence is in-memory soft state.
        A hub restart clears it; clients
        re-announce within one heartbeat.
    end note
```

## Key cross-cutting mechanisms

- **Authoritative ordering & persistence** live in `CollaborationService.ApplyAsync` →
  authorize (`RolePolicy`) → validate (size, causal readiness) → rate gate → commit (RGA/structured)
  → persist (`++ServerSequence`, append log, snapshot every *N*, `SaveChanges`).
- **Anchor rebasing** happens inside the text commit so comment ranges follow edits and orphan when
  their text is deleted.
- **Notifications** are delivered live via `IClientNotifier` when connected and always persisted.
- **Observability**: an OpenTelemetry meter exposes connected clients, ops applied, apply/transform
  duration, resync count, and rejected ops.
