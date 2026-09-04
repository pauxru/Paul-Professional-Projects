# ADR 0003 — SQLite-backed vector store; pgvector is the production adapter path

**Status:** Accepted (dev/CI); pgvector adapter is roadmap only &nbsp;
**Date:** 2025 &nbsp; **Owner:** Data

## Context

We need a vector store that:

1. Runs on a developer laptop with no infrastructure setup.
2. Can seed a corpus of ~20 documents / ~200 chunks and answer queries in
   milliseconds.
3. Presents an abstraction (`IVectorStore`) that can be swapped for a
   production-grade store (pgvector, Azure AI Search, Weaviate) without
   changing anything above it.

## Decision

The default `IVectorStore` implementation is `SqliteVectorStore`:

- Chunks are persisted in the same SQLite database as documents and prompts,
  under `DocumentChunk`. The embedding vector is stored as a `byte[]` blob
  produced by `Buffer.BlockCopy` of the `float[]`.
- On startup, chunks are loaded once into an in-memory `ConcurrentDictionary`
  keyed by chunk id. Cosine search is brute force — for a demo corpus of a
  few hundred vectors, brute force is exact and O(n·d) with n ≤ 500. This is
  faster and simpler than any approximate index.
- BM25 keyword search uses the same in-memory chunk pool and a hand-built
  `Bm25Index` refreshed under a lock whenever the pool changes.

**pgvector is the intended production adapter,** documented but not
implemented. The intended shape:

- `PgVectorStore : IVectorStore` inside `RagAssistant.Infrastructure.Pgvector`
  (a future project), using `Npgsql` and the `vector` extension.
- Cosine search delegated to `ORDER BY embedding <=> @q LIMIT @k`.
- BM25 keyword search delegated to Postgres full-text search plus `ts_rank_cd`.
- Permission filtering pushed into the SQL `WHERE` clause, still followed by
  the same `AclPostFilter` for defence in depth (ADR 0004).

## Consequences

**Positive.**

- Zero infra to run the project. `dotnet run` boots and seeds instantly.
- No external ANN library to audit or trust.
- Storage layer is uniform: documents, chunks, prompts, chat, feedback, usage
  all live in one SQLite file, which is easy to `xcopy` between dev machines.

**Negative.**

- Brute-force cosine does not scale past a few thousand chunks. That is
  acceptable for a portfolio demo but not for a real tenant with hundreds of
  thousands of chunks.
- SQLite blob-stored vectors are opaque to any tool that does not know the
  encoding. Documented in `docs/database-schema.md`.
- Rebuilding the BM25 index on every write is O(n·terms). Acceptable at
  ingestion cadence, not at query cadence.

## Alternatives considered

- **Faiss / HNSW in process.** Adds a native dependency and, for our corpus
  size, is measurably slower than brute force plus adds the risk of index
  rebuild after every ingest.
- **Cloud-only vector DB (Pinecone, Qdrant Cloud).** Requires an API key and
  network, which violates the "no paid API keys / no network" project rule.
