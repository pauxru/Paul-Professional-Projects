# Demo Script

1. Start the API with `dotnet run --project src\EnterpriseSearch.Api` and wait for `http://localhost:5022`.
2. Request a Development token with `search.manage` through `/api/v1/auth/token`.
3. Open `/openapi/v1.json` or use `scripts\demo.ps1`.
4. Call `/api/v1/analyze` with `<b>Café laptops</b>` and explain HTML stripping, accent folding, lowercasing, tokens, and stemming.
5. Search `catalogue` with `title:(laptop OR notebook) AND price:[100 TO 500]`, `HybridRrf`, category facet, highlights, and `explain=true`.
6. Point out BM25 per-term contributions, function values, and cursor pagination.
7. Search the `knowledge` alias with and without a `support-agent` group token; explain query-time ACL trimming and facet non-leakage.
8. Create `catalogue-v2`, refresh it, use the alias compare-and-swap endpoint, then describe rollback.
9. Send click events and show analytics/changed rank for a repeated query.
10. Run `--evaluate` and `--benchmark`, emphasizing that values are local synthetic measurements.
