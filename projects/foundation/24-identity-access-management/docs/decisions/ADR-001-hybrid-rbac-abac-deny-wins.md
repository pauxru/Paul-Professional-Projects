# ADR-001 — Hybrid RBAC/ABAC with deny-wins precedence

## Context

IGA reviewers need stable role-derived access and contextual controls such as department, classification, network zone, device trust, time, and MFA. A decision must be reproducible and explainable.

## Options

1. RBAC only.
2. ABAC only.
3. Hybrid RBAC/ABAC with first-match precedence.
4. Hybrid RBAC/ABAC with explicit deny winning globally.

## Decision

Use hybrid RBAC/ABAC. Direct, role, group, and active JIT paths establish effective entitlements. ABAC allow policies may also authorize. Every relevant policy is evaluated; any matching deny wins regardless of allow priority. Among policies with the same effect, higher priority then higher specificity is decisive. No grant or allow means default deny.

## Consequences

- Roles remain understandable to business owners.
- Context can constrain or augment standing access.
- The response includes every evaluated policy, match result, reason, decisive policy, and access derivation.
- Policy authors cannot override a deny by adding a higher-priority allow.

## Risks

- An overly broad deny can cause a large outage.
- An ABAC allow can grant without an RBAC path if authored broadly.
- Synchronous estate simulation becomes expensive at very large scale.

## Alternatives

First-match was rejected because list order is fragile. RBAC-only was insufficient for device/network/MFA controls. ABAC-only was rejected because it is difficult for reviewers to reason about at catalogue scale.
