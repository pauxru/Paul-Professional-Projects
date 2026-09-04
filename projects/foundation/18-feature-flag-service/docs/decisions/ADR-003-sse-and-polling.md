# ADR-003: Use SSE with ETag polling fallback

## Context
SDKs need near-live configuration while surviving proxies, disconnects, and control-plane restarts. Updates flow one way from server to client.

## Options
1. Fixed polling only.
2. WebSockets.
3. Server-sent events plus conditional polling.

## Decision
Use authenticated SSE as an invalidation signal, reconnecting with bounded exponential backoff. On every signal, fetch configuration through ETag/`If-None-Match`; a periodic poll uses the same endpoint as fallback.

## Consequences
The data protocol is simple HTTP, clients do not retain a bidirectional socket protocol, and 304 responses avoid retransmitting unchanged rules. A restart may drop an event but polling recovers it.

## Risks
The in-process broadcaster is process-local; production multi-node fan-out needs a durable pub/sub adapter. Some proxy configurations buffer SSE unless explicitly configured.

## Alternatives
WebSockets would support command/control but add lifecycle complexity. Polling alone is operationally simple but delays kill switches by the polling interval.
