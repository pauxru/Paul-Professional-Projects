# ADR-002: Use SHA-256 buckets and contiguous allocations

## Context
Rollouts must be deterministic across server and SDK, well distributed, and sticky when a percentage grows.

## Options
1. Random numbers per evaluation.
2. Runtime-specific hash APIs.
3. A documented SHA-256 byte-level algorithm with a fixed bucket range.

## Decision
Hash UTF-8 `flagKey|salt|contextKey` with SHA-256, read the first eight bytes as big-endian unsigned integer, then modulo 100,000. Evaluate weighted allocations from bucket zero upward.

## Consequences
The algorithm is reproducible in any SDK and supports basis-point weights. Expanding an allocation preserves its lower bucket cohort; a 10% rollout is a subset of 20%.

## Risks
Changing flag key or salt intentionally rebuckets users. Modulo bias is negligible for a 64-bit source and a 100,000 bucket destination but is documented rather than hidden.

## Alternatives
MurmurHash is faster but needs carefully pinned implementations. Rendezvous hashing helps experiments with dynamic variants but does not make simple percentage expansion as obvious.
