# ADR-001: Build the engine locally before adopting Lucene.NET or managed search

## Context
The case study's purpose is to demonstrate analyzer, posting, ranking, vector, and safety mechanics without an unavailable external search service. A production system would normally prefer Lucene.NET, Elasticsearch, OpenSearch, or Azure AI Search.

## Options
1. Call Elasticsearch/OpenSearch.
2. Embed Lucene.NET.
3. Use Azure AI Search.
4. Build a bounded educational engine in C# behind application contracts.

## Decision
Use option 4 for this reference implementation. `InvertedIndex`, `IScorer`, `IVectorIndex`, and `SearchCluster` expose seams that a production adapter can replace.

## Consequences
The code is inspectable and has direct tests for positions, BM25, phrase slop, fusion, and ACL-safe facets. It is not expected to match Lucene's compression, concurrency, language analysis, or distributed durability.

## Risks
A reader could confuse a teaching engine with a production search cluster. Documentation, API limits, and this ADR explicitly distinguish the prototype from a managed-service recommendation.

## Alternatives
For a production Contoso deployment, use Elasticsearch/OpenSearch where analyzer control and operational ownership are desired, or Azure AI Search when managed hybrid retrieval and Azure integration are preferred. Preserve the evaluation set and external API contract during migration.
