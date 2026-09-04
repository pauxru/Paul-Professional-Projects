# Architecture — Enterprise Search Platform

## Context
This modular monolith intentionally implements the concepts normally hidden behind Lucene, Elasticsearch, or Azure AI Search. It serves a fictional Contoso Retail catalogue and support knowledge base from independent, versioned logical indices.

## Modules and dependency rule
```mermaid
flowchart TB
  Api[EnterpriseSearch.Api] --> Infrastructure[EnterpriseSearch.Infrastructure]
  Api --> Application[EnterpriseSearch.Application]
  Infrastructure --> Application
  Application --> Domain[EnterpriseSearch.Domain]
```

`Domain` has no I/O or framework dependencies. `Application` owns query contracts, analysis, index data structures, rankers, vector interfaces, and ports. `Infrastructure` owns EF Core SQLite, time, JWT issuance, refresh hosting, and deterministic seed data. `Api` owns HTTP, composition, transport validation, and middleware.

## Indexing pipeline
```mermaid
flowchart LR
  API[Bulk / single / patch / delete] --> Channel[Bounded Channel]
  Channel --> Refresh[Timer or POST refresh]
  Refresh --> Analyze[HTML strip / normalize / fold / tokenize / filters]
  Analyze --> Postings[Field -> term -> doc TF + positions]
  Analyze --> Prefix[Prefix term map]
  Analyze --> Embed[Hashed TF-IDF-style vector]
  Embed --> Exact[Exact cosine]
  Embed --> IVF[Clustered IVF]
  Postings --> Snapshot[SQLite logical snapshot]
  Prefix --> Snapshot
```

Updates assign a higher document version. Old postings remain physically present but cannot match because their version does not equal the active document version. Deletes remove active document state and leave tombstones. Compaction rebuilds all derived structures from active documents and resets obsolete-posting accounting.

## Query execution
```mermaid
sequenceDiagram
  participant Caller
  participant API
  participant Parser
  participant Index
  participant Ranker
  Caller->>API: authenticated search request
  API->>API: size/clause/timeout validation
  API->>Parser: safe query string -> AST
  Parser->>Index: analyzer-aware postings / positions
  Index->>Ranker: BM25 lexical candidates
  API->>Index: exact or IVF cosine candidates
  API->>API: trim ACL groups and filter candidates
  API->>Ranker: RRF or normalized linear fusion
  Ranker-->>API: scores + per-term explanations
  API->>API: per-facet self-filter exclusion, snippets, cursor
  API-->>Caller: ProblemDetails or response
```

## Persistence and restart
SQLite holds index definitions, current versioned source documents, alias mappings, refresh generations, and timestamps. It is deliberately the durable logical source, not an attempt to emulate Lucene's compressed immutable segment files. Startup recreates posting, prefix, exact-vector, and IVF state from it. This keeps the custom engine transparent and makes reindexing deterministic.

## Production adapter mapping
The application contracts map directly to external search adapters:

| Local engine concept | Elasticsearch | Azure AI Search |
|---|---|---|
| `IndexDefinition` / alias | index mapping + alias | index + index alias / swap strategy |
| Analyzer chain | custom analyzer | lexical analyzer / custom skillset where supported |
| posting positions / BM25 | Lucene postings + similarity | built-in BM25 |
| `IVectorIndex` | `dense_vector` + HNSW | vector fields + HNSW/exhaustive KNN |
| RRF / linear fusion | retriever / rank features | hybrid query + semantic ranker |
| query-time ACL filter | terms filter on groups | filter expression on allowed groups |

The migration concern is behavioral parity: retain the golden evaluation set, analyzer tests, query limits, result schemas, and ACL/facet tests before changing adapters.
