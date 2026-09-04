# ADR-002 — Role hierarchy as a cycle-checked DAG

## Context

Enterprise roles compose and inherit other roles. Flattening inheritance at write time loses derivation evidence and makes changes difficult to reason about.

## Options

1. Disallow role inheritance.
2. Store a flattened permission set per role.
3. Store parent edges without validation.
4. Store a directed acyclic graph and traverse it at resolution time.

## Decision

Store `RoleId -> InheritedRoleId` edges and reject a new edge when the inherited role can already reach the child. Resolution performs depth-first traversal and retains each complete role path to an entitlement.

## Consequences

- Deep inheritance and diamond graphs resolve correctly.
- Multiple reasons for the same entitlement remain visible.
- Updates to a lower role are immediately reflected.
- Read cost grows with graph size but remains bounded by cycle prevention.

## Risks

- A database-level manual write could bypass service cycle detection.
- Extremely broad/deep graphs could increase decision latency.

## Alternatives

Flattening was rejected because it needs complex invalidation and discards the “why” path. Unvalidated graphs were rejected because cycles can cause non-termination and privilege ambiguity.
