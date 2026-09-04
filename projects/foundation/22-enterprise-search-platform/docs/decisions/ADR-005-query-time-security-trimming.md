# ADR-005: Apply security trimming at query time, including facets

## Context
Documents have group ACLs. A restricted document must never influence hits, suggestions exposed through a secured endpoint, or facet counts for an unauthorised caller.

## Options
1. Trust UI filtering.
2. Create one physical index per group/tenant.
3. Filter only returned hits.
4. Apply caller groups to retrieval candidates and every facet scope at query time.

## Decision
Use option 4. `SearchDocument.IsAllowedFor` is evaluated before ranking output and when calculating each facet. A facet excludes only its own selected filter while retaining all other filters and ACL trimming.

## Consequences
The implementation prevents a restricted category from leaking through a count. It avoids index explosion for groups with shared content.

## Risks
Large ACL lists add filter cost, and group claim freshness depends on the identity issuer. The current model is allow-list-only.

## Alternatives
For hard tenant boundaries, use per-tenant physical indices plus application-level routing. For production search clusters, use filtered aliases or mandatory ACL filter injection with independent authorization tests.
