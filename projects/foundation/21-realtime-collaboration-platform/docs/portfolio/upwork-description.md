# Upwork / Freelance Portfolio Description

## Short blurb (for a portfolio card)

**Real-time collaborative editing backend (.NET 10 + SignalR + CRDT).** Built a production-shaped
backend where multiple users co-edit documents live, with a from-scratch **RGA CRDT** for text whose
convergence is **proven by a randomised property test**. Includes reconnect/resync from a checkpoint +
operation log, presence with server-side coalescing, comment anchoring with rebasing, versioning with
time-travel and restore, hub rate limiting, and a dependency-free browser client. Builds and passes
**55 tests with zero external infrastructure**.

## Full description

I designed and built **Collab**, a real-time collaboration backend for a fictional operations team
(*Acme Manufacturing, fictional*) that co-edits runbooks and checklists. It is a self-directed
engineering case study focused on the hard part of collaborative editing: **making concurrent edits
converge correctly, and being honest about what the algorithm does and does not guarantee.**

**What I implemented**
- A **from-scratch RGA CRDT** (causal-tree) for plain text: character-identity nodes, tombstone
  deletes, Lamport-clock causality, and a buffer for out-of-order operations. Concurrent inserts and
  deletes converge to identical text on every replica.
- A **separate conflict strategy for structured data** (checklists): field-level **Last-Writer-Wins
  with version vectors** — because a list-of-records deserves a different strategy than free text.
- A **SignalR hub** with JWT auth, per-document rooms, server-authoritative sequencing, acks, and
  **reconnect/resync** (a client offline for K operations catches up from a checkpoint or the missed
  operation-log tail).
- **Presence** (cursors/selection, idle/eviction by heartbeat) with **server-side coalescing** so a
  fast typist doesn't flood the room.
- **Versioning**: append-only operation log + periodic snapshots, **time-travel** read at any
  version, named versions, **LCS diff**, and **restore implemented as a forward operation** (history
  is never rewritten).
- **Comments** anchored to text ranges that **rebase** as the text changes, with orphan detection.
- **Notifications** delivered live and persisted for offline users; **append-only audit**; **hub rate
  limiting** with disconnect-on-abuse.
- A **dependency-free web client**: I hand-implemented the SignalR JSON protocol over a WebSocket and
  ported the RGA to JavaScript, so two browser tabs co-edit and see each other's cursors with **no npm
  dependency**.

**How it's proven**
- `dotnet build -c Release`: 0 warnings, 0 errors. `dotnet test -c Release`: **55 tests pass** (30
  unit + 25 integration) on **SQLite with no Docker/DB/Redis**. Integration tests drive **real
  SignalR clients** against an in-memory test server, including two-client convergence, presence
  propagation, resync-from-checkpoint, authorization denials, rate-limit disconnect, and offline
  notification delivery.
- Convergence is verified by a **randomised property test** (fixed seed, multiple clients, shuffled
  application orders → identical replicas), backed by exhaustive transform-pair unit tests.

**Engineering judgement on display**
I wrote a dedicated `conflict-resolution.md` that states the guarantees **and the limitations**
plainly (CRDTs converge but don't do semantic merge; concurrent runs can interleave; LWW drops the
loser of a concurrent field write; ordering is logical, not wall-clock). Scale-out via a Redis
backplane is **configured but labelled UNVERIFIED** because the build host has no Redis. That honesty
is deliberate.

**Stack:** .NET 10, ASP.NET Core Minimal APIs, SignalR, EF Core (SQLite default, Postgres by config),
JWT bearer, OpenTelemetry, xUnit.

> This is a demonstration project with fictional data. Authentication is intentionally demo-grade and
> documented as such; the code is structured so the demo pieces can be swapped for production
> equivalents (OIDC, managed keys, TLS, a backplane) without reworking the core.
