# ADR-004: Use clustered IVF as the approximate vector-search demonstration

## Context
The project needs a no-network vector baseline and a measurable approximate alternative. True HNSW is powerful but substantially more code and tuning surface than is useful for this focused implementation.

## Options
1. Exact cosine only.
2. Full HNSW implementation.
3. Clustered inverted file (IVF) search.
4. External vector database.

## Decision
Keep exact cosine as the correctness baseline and implement deterministic k-means-style IVF clusters, probing the best configured centroids.

## Consequences
The benchmark measures top-k overlap (recall) and latency against exact cosine. Fewer probes examine fewer candidates but can miss true neighbours; this trade-off is explicit in code and performance documentation.

## Risks
Cluster quality is sensitive to corpus shape and deterministic hash embeddings are not semantic foundation-model embeddings. IVF should not be represented as a production ANN benchmark.

## Alternatives
Adopt HNSW in Elasticsearch/Azure AI Search, tune `efSearch`/oversampling using the golden set, or use exhaustive KNN for small high-recall corpora.
