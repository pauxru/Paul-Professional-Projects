# Portfolio Summary

Enterprise Search Platform with Hybrid Retrieval is a self-directed engineering case study for a fictional Contoso Retail corpus. It demonstrates the machinery below a search SDK: analyzer stages, positional inverted indexes, BM25 arithmetic, safe AST parsing, query-time ACL trimming, facets, local vector retrieval, IVF approximation, fusion, evaluation, and operational controls.

The most inspectable features are `/api/v1/analyze`, `explain=true`, the hand-computed BM25 test, alias compare-and-swap, and the facet security test. It runs locally with SQLite and no external service.
