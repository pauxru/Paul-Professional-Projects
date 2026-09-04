# ADR-003: Persist logical index snapshots in SQLite and compact derived state

## Context
The host provides SQLite but no PostgreSQL or search cluster. The engine needs durable source documents, index metadata, aliases, refresh generations, and a clear deletion model.

## Options
1. Keep all index state only in memory.
2. Serialize opaque binary posting segments.
3. Persist logical documents/metadata in SQLite and rebuild derived structures.
4. Require an external search server.

## Decision
Use option 3. SQLite tables retain the logical active index snapshot; startup rebuilds postings, prefix terms, exact vectors, and IVF clusters. Updates/deletes leave versioned stale postings/tombstones in memory until explicit compaction.

## Consequences
The persistence format is queryable, portable, and easy to inspect. Cold start cost is proportional to the document snapshot, not a binary segment load.

## Risks
Replacing all document rows for a refreshed snapshot is not suitable for large shards, and SQLite is not a distributed write coordinator.

## Alternatives
Production adapters can use immutable Lucene/Elasticsearch segments, object-store snapshots, or PostgreSQL source-of-truth plus managed search indexing.
