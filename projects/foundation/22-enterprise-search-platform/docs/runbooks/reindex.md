# Runbook: Zero-Downtime Reindex

## Trigger
Use after changing analyzer configuration, mappings, source data, ranking assumptions, or when compaction/rebuild is safer than a partial repair.

## Preconditions
- Obtain a Development/production-equivalent `search.manage` token.
- Preserve the source document export and current alias target.
- Run the relevance harness before and after the change.

## Procedure
1. Inspect current aliases and stats:
   ```powershell
   Invoke-RestMethod http://localhost:5022/api/v1/indices -Headers $headers
   ```
2. Create a new concrete version, for example `catalogue-v2`, with the intended field/analyzer definition.
3. Bulk load source documents with `POST /api/v1/indices/catalogue-v2/documents/bulk`. The bounded queue can return `429`; pause and call refresh before retrying.
4. Explicitly refresh, inspect stats, sample query explanations, ACL-safe facets, and run:
   ```powershell
   Invoke-RestMethod 'http://localhost:5022/api/v1/eval/run?index=catalogue-v2' -Method Post -Headers $headers
   ```
5. Atomically swap only if the expected old target still matches:
   ```json
   POST /api/v1/indices/aliases/swap
   { "alias":"catalogue", "expectedCurrent":"catalogue-v1", "next":"catalogue-v2" }
   ```
6. Smoke test reads through `catalogue`; then delete `catalogue-v1` only after rollback risk has passed.

## Rollback
Swap the alias back using the current expected target while the prior version remains. Do not drop the old index before verification.

## Evidence
Record stats, evaluation JSON, correlation IDs, and any changed analyzer definition in the change record.
