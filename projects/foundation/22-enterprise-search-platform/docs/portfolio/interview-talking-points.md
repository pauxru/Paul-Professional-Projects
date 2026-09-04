# Interview Talking Points

## Why build search internals?
Calling a search API demonstrates integration. This project demonstrates understanding of token positions, term statistics, scoring saturation, query execution limits, hybrid candidate fusion, and ACL/facet leakage.

## What fails at 3am?
Queue saturation, slow wildcard/deep-page queries, stale refreshes, bad analyzer changes, alias races, zero-result spikes, and ACL leaks. The bounded queue, timeout/limits, correlation IDs, runbooks, compare-and-swap aliases, evaluation harness, and security tests address those failure modes.

## Why SQLite rather than Elasticsearch?
The host has no search cluster, and transparent local execution is the learning goal. SQLite stores logical snapshots; an ADR maps ports to Elasticsearch/Azure AI Search for a production deployment.

## How is ranking validated?
A hand-computed BM25 fixture validates math. A 30-query graded golden set compares keyword, vector, weighted RRF, and linear hybrid modes; a regression requires weighted RRF not to regress nDCG@10 versus keyword.

## What is the most subtle security point?
Filtering hits is insufficient: a restricted document can leak through facet counts. The facet code applies caller ACLs and excludes only the facet's own selected filter, with a dedicated test.

## What would change for production?
Use managed identity/OIDC, durable queues and analytics, language-aware analyzers, HNSW/vector models, compressed/distributed segments, audit retention, monitoring/alerting, and human relevance labels before capacity or relevance claims.
