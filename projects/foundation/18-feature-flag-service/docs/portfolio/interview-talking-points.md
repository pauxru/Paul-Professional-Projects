# Interview Talking Points

## Why local evaluation?
It removes control-plane latency and availability from the request path. The trade-off is distributing rules safely, which drives client/server key exposure boundaries, SSE invalidation, ETag polling, and an offline cache.

## How do cohorts stay stable?
The code specifies every byte-level part of SHA-256 bucketing and assigns contiguous basis-point ranges. The 10,000-key parity fixture catches an SDK implementation drift; the expansion test proves 10% is a subset of 20%.

## What fails at 3am?
SSE disconnects, a bad rollout, stale caches, event queue overflow, and unauthorized production changes. The SDK uses cache/backoff/polling/defaults; operations have kill-switch and rollback runbooks; governance has four-eyes and audit trails.

## What would you scale next?
Use a transactional outbox and distributed pub/sub for cross-node invalidation, keyed secure storage/rotation, OIDC, normalized/searchable rule persistence where operationally necessary, retention/HLL for analytics, and optimistic concurrency for concurrent editors.

## Why SQLite snapshots?
A complete environment ruleset is naturally transported to the SDK and can be atomically copied/reverted. This makes the portfolio focus the evaluation and resilience mechanics rather than obscuring them behind a complex schema.
