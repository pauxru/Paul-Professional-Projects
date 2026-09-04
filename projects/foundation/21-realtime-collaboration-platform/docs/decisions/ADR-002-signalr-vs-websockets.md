# ADR-002 — Transport: SignalR vs raw WebSockets

- **Status:** Accepted
- **Date:** 2026-09-03

## Context

The realtime surface needs bidirectional, low-latency messaging between browsers and the server:
join/leave document rooms, submit operations, receive broadcasts and acks, presence, and resync.
The host constraints are firm: **.NET 10 only, no external infrastructure, SQLite default, must run
under `WebApplicationFactory` in tests.** We need something that a browser can drive and that the
integration tests can exercise against the in-memory test server.

## Options considered

### Option A — ASP.NET Core SignalR — **chosen**
- **Pros:** First-party for .NET; gives us hubs, per-connection principals, groups (perfect for
  per-document rooms), automatic reconnection on the official client, pluggable transports
  (WebSockets, Server-Sent Events, Long Polling) with negotiation and graceful fallback, and a
  documented **Redis backplane** for scale-out. Integrates with JWT bearer auth, including the
  `access_token` query-string convention browsers need on the WebSocket handshake. Works over the
  `TestServer` transport in integration tests.
- **Cons:** A framing/protocol abstraction we do not fully control; the JSON hub protocol and the
  handshake add a little opacity; sticky sessions / backplane required for multi-node.

### Option B — Raw `System.Net.WebSockets` endpoint
- **Pros:** Total control of the wire protocol; no negotiation; minimal dependencies.
- **Cons:** We would hand-roll connection lifecycle, grouping, reconnection, backpressure, heartbeat,
  and a message protocol — reinventing most of SignalR, with more bugs and less test leverage. No
  built-in backplane story.

### Option C — gRPC / gRPC-Web streaming
- **Pros:** Efficient binary streaming, strong typing.
- **Cons:** Browser support needs gRPC-Web + a proxy; bidirectional streaming in browsers is awkward;
  heavier than the problem needs; weaker fit for "many small fan-out broadcasts to a room".

## Decision

Use **SignalR** for the hub (`/hubs/collaboration`), authenticated with JWT (bearer header, or
`access_token` query string for the browser WebSocket handshake), with per-document **groups**.
Keep the wire contract explicit (`JoinDocument`, `SubmitOperation`, `UpdatePresence`, `Ping`,
`ResyncDocument`; events `OperationApplied`, `PresenceChanged`, `Rejected`, `Resynced`,
`CommentAdded`) so the protocol is legible even though SignalR frames it.

To prove the protocol is not magic, the **web client hand-implements the SignalR JSON hub protocol
over a raw WebSocket** (handshake, `0x1e` record-separator framing, invocation/completion/ping
messages) with **no npm dependency**. So we get SignalR's server-side ergonomics *and* a transparent,
dependency-free client.

## Consequences

**Positive:** Minimal transport code; groups map exactly to document rooms; JWT integration is
first-party; integration tests drive real SignalR clients over the test server (Long Polling
transport) with hard cancellation timeouts. A backplane exists as a supported scale-out path.

**Negative:** Multi-node requires a backplane + sticky routing (configured but unverified — see
ADR-005). The hand-rolled JS client must track the SignalR protocol if it changes.

## Risks and mitigations
- **Risk:** WebSocket-over-`TestServer` is finicky. **Mitigation:** integration tests use the
  **Long Polling** transport against the test server, which authenticates via the bearer header and is
  reliable; production browsers use WebSockets.
- **Risk:** Protocol drift between our JS client and SignalR. **Mitigation:** the client targets the
  documented, stable JSON protocol v1; behaviour is exercised by two-tab manual demo and mirrored by
  the C# integration client.

## Alternatives not pursued
Raw WebSockets (B) — rejected as re-implementing SignalR. gRPC-Web (C) — rejected for browser
friction and poor fit to room fan-out.
