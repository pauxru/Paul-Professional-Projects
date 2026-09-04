# Runbook: Slow Queries

## Symptoms
Rising `search.query.latency`, request timeouts (`408`), `429` rate-limit responses, or user reports of delayed search.

## Triage
1. Capture `X-Correlation-Id`, query shape, index alias target, mode (keyword/exact/IVF/hybrid), result count, and caller groups. Do not copy credentials or sensitive raw queries into unrestricted logs.
2. Inspect `/api/v1/indices/{name}/stats` for document growth, tombstones, posting count, and stale refresh time.
3. Check `/api/v1/analytics/summary` for zero-result change and top-query concentration.
4. Reproduce with the smallest safe query. Compare keyword with vector/hybrid and exact with IVF.

## Immediate containment
- Reject or rewrite leading/broad wildcards; limits cap expansions at the configured value.
- Require `search_after` rather than deep `from` pagination.
- Lower page size or remove expensive facets/highlights for diagnostic queries.
- Let the rate limiter protect the host; do not disable clause or timeout limits during an incident.

## Recovery and prevention
Compact a high-tombstone index after checking source snapshot integrity. If analyzer/schema changes are needed, follow `reindex.md` and alias-swap a new version. Tune IVF probes only against the relevance set and measure recall before reducing candidates.
