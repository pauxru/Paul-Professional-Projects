# Upwork Description

## Enterprise Search Platform with Hybrid Retrieval — self-directed engineering case study

**Problem:** A fictional retail catalogue and support corpus need secure, relevant, explainable discovery without an external search cluster.

**Built:** A .NET 10 search reference implementation with per-field analyzers, positional BM25, safe Boolean/query-string parsing, facets, deterministic vector/IVF search, hybrid RRF/linear fusion, zero-downtime aliases, click feedback, SQLite snapshots, and query-time ACL trimming.

**Engineering focus:** ranking transparency, abuse-resistant query execution, measured ANN trade-offs, relevance regression testing, and operational recovery.

**Stack:** C#, ASP.NET Core, EF Core SQLite, JWT, OpenTelemetry, xUnit.

**Verification:** release build/test, 30-query synthetic relevance evaluation, and 5,000-document local benchmark are recorded in the repository.

This is a self-directed portfolio project, not client work.
