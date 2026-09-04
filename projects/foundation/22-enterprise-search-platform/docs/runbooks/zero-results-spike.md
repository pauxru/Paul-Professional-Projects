# Runbook: Zero-Results Spike

## Symptoms
`search.zero_result.count` rises or `/api/v1/analytics/summary` shows a higher zero-result rate.

## Triage
1. Confirm whether the spike is catalogue, knowledge base, a caller group, a new alias target, or a particular query family.
2. Use `/api/v1/analyze` to inspect normalization, stop-word removal, stemming, synonym expansion, and offsets for representative safe queries.
3. Check aliases and refresh stats; queued documents are intentionally invisible until refresh.
4. Compare keyword, exact vector, and hybrid modes on the golden set and affected query samples.
5. Verify ACL group claims. A result that is correctly trimmed must not be "fixed" by bypassing authorization.

## Recovery
Call the explicit refresh endpoint when newly indexed content is expected. Restore or swap a known-good alias if a reindex mapping caused the regression. Add safe synonyms or correction candidates only after reviewing false-positive risk. Re-run evaluation and record query-level changes.

## Prevention
Monitor zero-result rate by index and ACL cohort, retain a curated judgement set, test analyzer changes, and distinguish genuine inventory/content gaps from spelling, authorization, or indexing-lag causes.
