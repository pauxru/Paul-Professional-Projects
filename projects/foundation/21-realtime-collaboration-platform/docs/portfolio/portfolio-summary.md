# Portfolio Summary — Collab (Real-Time Collaboration Backend)

**Type:** Self-directed engineering case study.
**Fictional context:** a collaborative operations workspace for *Acme Manufacturing (fictional)*,
where teams co-edit runbooks, incident notes, and checklists in real time.

## What it is

A production-shaped **.NET 10** backend for real-time collaborative editing, built as a modular
monolith (Domain / Application / Infrastructure / Api). It demonstrates **correct concurrent editing
with an honestly documented conflict model** — not a "pretend Google Docs".

## The engineering headline

I implemented a **from-scratch RGA CRDT** (Replicated Growable Array, a causal-tree CRDT) for plain
text, and a **separate field-level Last-Writer-Wins with version vectors** for structured documents —
because different data shapes deserve different conflict strategies. Convergence is not asserted by
hand-waving; it is **proven by a randomised property test** (fixed seed, M clients, operations
applied in shuffled orders, all replicas must converge) plus exhaustive transform-pair unit tests.

The most important artifact is `docs/conflict-resolution.md`: a precise, blunt statement of **what
the algorithm guarantees and what it does not** (it converges; it does not do semantic merge; it can
interleave concurrent runs; LWW drops the loser of a concurrent field write). That honesty is the
senior signal.

## What works (verified)

- **RGA CRDT text editing** with causal buffering for out-of-order operations, server-authoritative
  sequencing, and optimistic local editing that reconciles by **merge, not transform**.
- **SignalR hub** (`/hubs/collaboration`) with JWT auth, per-document rooms, acks with server
  sequences, and **reconnect/resync from a checkpoint + operation log**.
- **Presence** with cursor/selection, idle/eviction by heartbeat, and **server-side coalescing** so
  20 cursor moves/second become one broadcast.
- **Versioning & history**: append-only operation log + periodic snapshots, time-travel read, named
  versions, LCS diff, and **restore-as-forward-operation** (never a history rewrite).
- **Comments** anchored to text ranges with **anchor rebasing** and orphan detection.
- **Notifications** (mentions) delivered live over SignalR and persisted when offline.
- **Audit** append-only trail; **rate limiting** on the hub with disconnect-on-abuse.
- A **dependency-free web client** (hand-rolled SignalR JSON protocol + RGA port) where two browser
  tabs co-edit and see each other's cursors.

## Proof

- `dotnet build -c Release` → **0 warnings, 0 errors**.
- `dotnet test -c Release` → **55 passed, 0 failed** (30 unit + 25 integration), on SQLite with **no
  external infrastructure**. Real `Microsoft.AspNetCore.SignalR.Client` connections drive the
  integration tests against `WebApplicationFactory`.

## Scope & honesty

- Auth is **demo-grade** (password-less email→JWT) on purpose; the security review lists this and
  other explicit non-claims.
- **Redis backplane scale-out is configured but UNVERIFIED** (no Redis/Docker on the host); single
  node is the only verified topology.
- CRDT limitations (interleaving, tombstone accumulation, plain-text only, logical clock) are
  documented, not hidden.

## Tech stack

.NET 10, ASP.NET Core Minimal APIs, SignalR, EF Core (SQLite default; Postgres by config swap), JWT
bearer, OpenTelemetry metrics, xUnit. ~5.9k lines across ~86 source files.
