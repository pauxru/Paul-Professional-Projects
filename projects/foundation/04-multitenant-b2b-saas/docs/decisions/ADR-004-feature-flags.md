# ADR-004 — Deterministic feature evaluation and tenant cache

**Status:** Accepted
**Date:** 2026-09-03

## Context

Flags require emergency shutdown, tenant defaults, user exceptions and gradual rollout. Evaluation must be stable across requests and never share cached decisions across tenants.

## Options

1. Random percentage choice on every request.
2. Stable hash with global cache keys.
3. Stable hash of `(flagKey,userId)` with tenant-namespaced/versioned cache.
4. External feature-management SaaS.

## Decision

Evaluation order is kill switch, user override, disabled base state, then stable SHA-256 bucket. Cache keys include tenant namespace, flag, user and flag version. Flag/override changes are audited. `IAppCache` accepts logical keys only and rejects caller-supplied physical keys.

## Consequences

- A user consistently remains in or out of a rollout.
- Kill switch always wins.
- Version changes naturally avoid stale evaluation entries.
- The local cache needs no external service.

## Risks

- Versioned entries remain until TTL and consume memory.
- In-process caches differ across replicas.
- User IDs become inputs to rollout, so identity stability matters.

## Alternatives

Random choice produces a poor experience and non-reproducible tests. Global keys risk tenant disclosure. A managed flag service is appropriate for production teams needing non-developer workflows, but the local design exposes the evaluation mechanics this case study intends to demonstrate.
