# Runbook — Hub connection storm

**Symptom:** a sudden spike in SignalR connections / reconnections to `/hubs/collaboration`; rising
CPU, memory, or thread-pool starvation; clients see slow joins, delayed `OperationApplied`
broadcasts, or repeated disconnects.

## 1. Confirm it is a storm

Check the collaboration metrics (OpenTelemetry meter `Collab.Collaboration`):

- `collab.clients.connected` (gauge) — is it far above normal and climbing?
- `collab.operations.rejected` — climbing rejections suggest per-connection rate limits are firing
  (clients retrying aggressively).
- `collab.apply.duration` (histogram) — apply latency rising indicates the server is saturated.

Correlate with the process: high CPU, growing managed memory, ThreadPool queue length. Look for many
connections from a **single** subject/IP (a hot loop in one client) vs. a **broad** spike (mass
reconnect after a network blip or a deploy).

## 2. Immediate mitigations

1. **Let the built-in limits work.** Each connection already has a token-bucket operation limiter
   (`OperationsPerSecondPerConnection`, `OperationBurst`) and is disconnected after
   `MaxViolationsBeforeDisconnect` violations. A single abusive client is bounded automatically and is
   proven by `HubRateLimitTests`.
2. **Tighten the caps** (config, no redeploy of code needed) if a broad storm is overwhelming the
   node — lower `Collaboration:OperationsPerSecondPerConnection` and `OperationBurst`, lower
   `MaxViolationsBeforeDisconnect`, and restart the instance to pick up config.
3. **Shed load at the edge.** Because the app host is single-node and the REST limiter deliberately
   **excludes** the hub path, connection-count limiting is an **edge/deployment** responsibility: use
   the load balancer / reverse proxy to cap concurrent connections per IP and to enable
   backpressure/queueing on the hub route.
4. **Stagger reconnects.** If the storm is a thundering herd after an outage, the official SignalR
   client uses randomised/backoff reconnect; ensure custom clients (including the vendored
   `wwwroot/hub-client.js`) apply jittered backoff rather than a tight reconnect loop.

## 3. If a single client is looping

- Identify the subject from logs/correlation ids. The per-connection limiter will already be
  rejecting and eventually aborting it; if it reconnects instantly in a loop, block the IP/subject at
  the edge until the client is fixed.

## 4. After the storm

- Presence is **soft state**: any connections dropped during mitigation re-announce presence within
  one heartbeat interval; no data is lost (documents are reconstructed from the log/snapshots).
- Review `collab.operations.rejected` and apply-duration trends; if normal load is near the caps,
  plan the **scale-out path** (ADR-005: Redis backplane + shared presence + sticky sessions) — noting
  it is currently **UNVERIFIED** and needs the presence/limit gaps closed before multi-node use.

## 5. Prevention

- Keep the per-connection limiter enabled (it is on by default).
- Put a connection-rate / max-connections policy on the edge proxy.
- Ensure every client uses jittered exponential backoff for reconnects.
- Watch the connected-clients gauge and alert on abnormal slope, not just absolute value.
