# ADR-004 — Presence throttling and coalescing

- **Status:** Accepted
- **Date:** 2026-09-03

## Context

Presence (who is in a document, cursor/selection positions, active/idle status) changes very
frequently: a cursor can move tens of times per second while someone types or selects. Naïvely
broadcasting every presence update to every participant produces an N×M storm — 20 cursor moves per
second from each of K participants fanned out to K−1 others. That wastes bandwidth and CPU and can
starve the actual operation traffic. Presence must also degrade gracefully across a hub restart.

## Options considered

### Option A — Broadcast every presence update immediately
- **Pros:** Simplest; lowest perceived latency.
- **Cons:** O(updates × participants) messages; a single fast typist floods the room. Rejected.

### Option B — Client-side throttling only
- **Pros:** Reduces messages at the source.
- **Cons:** Trusts every client to behave; a buggy or hostile client can still flood; the server
  still fans out whatever it receives. Insufficient on its own.

### Option C — Server-side coalescing with a periodic flush — **chosen**
- **Pros:** The server stores the *latest* presence per connection and a **background flusher**
  broadcasts a coalesced snapshot per document at a fixed cadence
  (`Collaboration:PresenceThrottleMs`, default **150 ms**). 20 cursor moves in a 150 ms window become
  **one** broadcast carrying the final position. Also the natural place to compute **idle** (no
  heartbeat for `PresenceIdleSeconds`, default 20 s) and **eviction** (no heartbeat for
  `PresenceEvictSeconds`, default 60 s). Server-authoritative, so it is robust to misbehaving clients.
- **Cons:** Adds up to one flush-interval of presence latency (≤150 ms) — imperceptible for cursors;
  a background hosted service to own.

## Decision

Coalesce presence **on the server**. Each `UpdatePresence` mutates in-memory per-connection state;
a hosted **`PresenceFlusherService`** wakes every `PresenceThrottleMs`, marks idle/evicts stale
connections by heartbeat age, and broadcasts **one** `PresenceChanged` snapshot per document that had
changes. `Ping` refreshes a connection's heartbeat. The web client additionally throttles its own
`UpdatePresence` sends (~120 ms) as a courtesy, but the server is the enforcement point.

## Consequences

**Positive:** Broadcast volume is bounded by (documents × flush rate), not (participants × cursor
speed). Idle detection and eviction are centralised and testable. Robust against chatty/hostile
clients. Operation traffic is not starved by presence chatter.

**Negative:** Presence is **in-memory**, so a **hub restart clears it** — every client re-announces
presence on its next heartbeat/update and the room repopulates within one heartbeat interval. This is
the documented, accepted behaviour: presence is *soft state*, reconstructable from clients, and is
deliberately **not** persisted (persisting ephemeral cursor positions has no value and adds write
load). Multi-node presence would need the backplane (ADR-005).

## Risks and mitigations
- **Risk:** Flusher stalls → presence freezes. **Mitigation:** it is a lightweight periodic loop with
  no blocking I/O; failures are logged; document operations are unaffected because they do not depend
  on it.
- **Risk:** A client that stops sending heartbeats lingers. **Mitigation:** heartbeat-age eviction
  removes it after `PresenceEvictSeconds`.

## Alternatives not pursued
Immediate broadcast (A) — rejected for the storm. Client-only throttling (B) — rejected as
unenforceable. Persisting presence to the database — rejected as write amplification for ephemeral
data with no recovery value.
