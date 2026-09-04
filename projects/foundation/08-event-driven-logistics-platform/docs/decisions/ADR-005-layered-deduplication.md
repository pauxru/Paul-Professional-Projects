# ADR-005 — Layer cache and database deduplication

## Status

Accepted.

## Context

Devices and gateways retry when acknowledgements are lost. The common duplicate should be cheap to suppress, but process-local memory cannot be the source of truth across restart or scale-out. Reusing a sequence number with different content must be distinguished from a harmless retry.

## Options

1. Accept every ping.
2. Use only an in-memory set.
3. Use only a database unique index.
4. Use an expiring bounded LRU before a database unique index, retaining a content hash at both layers.

## Decision

Use option 4. Canonical telemetry fields are formatted invariantly and hashed with SHA-256. The cache key is `(vehicleId, sequenceNumber)` and stores the hash:

- key absent: continue and remember;
- key/hash equal: cache duplicate;
- key equal/hash different: conflict.

Cache misses are batch-checked/persisted in SQLite. A unique index on `(VehicleId, SequenceNumber)` is the final authority, and `ContentHash` classifies same-content retry versus conflict.

## Consequences

- Hot retries avoid database I/O.
- Cache capacity/expiry bound memory and permit eventual eviction.
- Restart and multi-instance races remain safe at the unique index.
- The API reports cache duplicates, database duplicates and conflicts separately.
- Batch persistence keeps the 10,000-ping endpoint practical.

## Risks

- Canonicalization changes could alter hashes for semantically equivalent events.
- A compromised device can deliberately reuse sequences to create conflicts.
- The local process-wide SQLite write gate is not a distributed concurrency control.
- SHA-256 proves content equality, not device authenticity.

## Alternatives

Database-only deduplication is simpler but increases write-path load. A distributed Redis cache could reduce misses across replicas, but the database constraint would still be required. Signed device payloads are complementary: signatures establish origin/integrity, while idempotency establishes processing uniqueness.
