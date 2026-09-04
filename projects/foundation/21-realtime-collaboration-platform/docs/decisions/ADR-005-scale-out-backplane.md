# ADR-005 — Scale-out via a SignalR backplane (configured, UNVERIFIED)

- **Status:** Accepted (design intent) — **UNVERIFIED on this host**
- **Date:** 2026-09-03

## Context

A single API process holds two pieces of **in-memory** realtime state: SignalR group membership (who
is connected to which document) and presence. That is correct and fast for one node. To run more than
one instance behind a load balancer — for availability or throughput — a broadcast from node A must
reach clients connected to node B. The standard answer for SignalR is a **backplane** (Redis) that
relays hub messages between servers.

The host for this project has **no Redis and no Docker**, and the prime directive forbids requiring
external infrastructure. So this ADR records the **design and configuration** for scale-out and is
explicit that it has **not been run or verified**.

## Options considered

### Option A — Single node only, document it as a limitation
- **Pros:** Nothing to build; matches the zero-infra constraint exactly.
- **Cons:** No horizontal scale or rolling-restart continuity story at all.

### Option B — Redis backplane (`Microsoft.AspNetCore.SignalR.StackExchangeRedis`) — **chosen design**
- **Pros:** First-party, well-documented; relays hub invocations across nodes so groups and broadcasts
  work cluster-wide; the recommended SignalR scale-out path.
- **Cons:** Requires a Redis server (absent here); adds a network hop; presence coalescing and the hub
  rate limiter remain **per-node** unless also moved to shared state.

### Option C — Azure SignalR Service
- **Pros:** Fully managed fan-out, no backplane to run.
- **Cons:** Paid cloud dependency; contradicts the local, zero-infra, reproducible-by-any-reviewer
  goal. Rejected for this case study.

## Decision

Design for a **Redis backplane** and document the exact wiring, but **default to single-node** and
**do not claim it works**. The intended configuration (guarded behind a config switch, off by default)
is:

```csharp
// Enabled only when Backplane:Provider = "Redis" AND a connection string is supplied.
builder.Services
    .AddSignalR()
    .AddStackExchangeRedis(cfg["Backplane:Redis"], o => o.Configuration.ChannelPrefix = "collab");
```

For multi-node correctness the following must **also** be addressed (noted, not implemented):

- **Presence** must move from per-process memory to shared state (e.g. Redis with per-connection TTL
  keyed by document) or be derived from backplane presence events; otherwise each node only sees its
  own participants.
- **Hub rate limiting** is per-connection and therefore per-node already (acceptable), but any
  *global* per-user cap would need shared counters.
- **Sticky sessions** (or the negotiation/redirect the SignalR client handles) so a connection's
  transport stays on one node.

## Consequences

**Positive:** There is a credible, first-party scale-out path and it is written down honestly.
**Negative / explicit non-claims:**
- The compose file ships a Redis service **for illustration only**; it has **not** been started.
- The backplane code path is **not** exercised by any test and is **not** enabled by default.
- Presence and any global limits are **not** yet cluster-correct.

> **Honesty statement:** Redis backplane scale-out is **configured and documented but UNVERIFIED**.
> No multi-node run has been performed on this host because Redis/Docker are unavailable. Treat the
> single-node deployment as the only verified topology.

## Risks and mitigations
- **Risk:** A reader assumes multi-node works out of the box. **Mitigation:** this ADR, the README
  "Running with Docker", and `docker-compose.yml` all label it UNVERIFIED.
- **Risk:** Presence appears broken across nodes. **Mitigation:** documented above as the known gap to
  close before any real multi-node deployment.

## Alternatives not pursued
Azure SignalR Service (C) — rejected as a paid cloud dependency inconsistent with the zero-infra,
reproducible goal. Single-node-forever (A) — rejected as under-selling a straightforward,
well-understood scale path, but it **is** the only *verified* topology today.
