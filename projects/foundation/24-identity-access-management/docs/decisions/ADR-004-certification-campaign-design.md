# ADR-004 — Materialized certification campaign items

## Context

Reviewers need a stable list of user-entitlement decisions, assigned reviewers, progress, deadline enforcement, and evidence showing why each entitlement existed at campaign creation.

## Options

1. Query live access on every campaign page.
2. Materialize one item per raw grant.
3. Materialize one item per effective `(user, entitlement)` with derivation paths.

## Decision

At creation, resolve effective access and persist one item per `(user, entitlement)` matching application, department, or risk scope. Assign the manager or entitlement owner and snapshot all derivation paths. Bulk decisions update items. Deadline processing auto-revokes pending items.

Revocation creates a `UserEntitlementExclusion`, not just a direct-grant update, so inherited and group-derived access is also removed.

## Consequences

- Campaign progress is stable and cheap to query.
- Review evidence survives later role changes.
- Reviewers see effective access rather than implementation-level grants.
- Exclusions require a deliberate restoration path.

## Risks

- Snapshot evidence can differ from current access during a long campaign.
- Large campaigns require asynchronous generation in production.
- Broad exclusions can hide newly legitimate inherited access until reviewed.

## Alternatives

Live querying was rejected because the review population would move under reviewers. Per-grant items were rejected because one entitlement could generate duplicate/conflicting decisions.
