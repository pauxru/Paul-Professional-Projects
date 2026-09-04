# ADR-002: Canonical JSON serialisation

*Status*: Accepted
*Date*: 2026-01-15

## Context

The entire integrity story reduces to one question: *given the payload bytes, what were the
bytes we hashed?* Any non-determinism in serialisation (key order, whitespace, number format,
Unicode escape variants) makes verification impossible.

## Decision

Implement a minimal deterministic serialisation in `AuditPlatform.Domain.Serialization.CanonicalJson`,
with the following rules and a matching test suite:

1. Object keys are sorted lexicographically by their UTF-8 code-point sequence (case-sensitive).
2. No insignificant whitespace.
3. Strings pass through `JavaScriptEncoder.Default` (the strict encoder) so ASCII is verbatim
   and non-ASCII is escaped consistently.
4. Numbers: integers when the value is representable as `int64`; decimals otherwise, rendered
   via `G29` with trailing-zero strip. NaN/Infinity are rejected.
5. Booleans and null are the JSON literals.
6. Output is UTF-8 with no BOM.
7. Array order is preserved (arrays are ordered data).

## Rationale for a bespoke spec rather than RFC 8785 (JCS)

- **Auditable**: 60 lines of self-contained C# is easier to review than a several-hundred-line
  reference implementation.
- **Portable**: any language can reproduce this by following the seven bullet points above.
- **Sufficient**: our inputs are always audit event payloads. We don't need JCS's full number
  spec.

## Consequences

- Every hash test uses byte-for-byte equality against a hand-canonicalised expected value.
- Adding a feature to the audit event schema must not change the canonicalisation of existing
  values (that would break historical verification).

## Alternatives considered

- **RFC 8785 JSON Canonicalization Scheme** — great option; deferred because the smaller
  spec is easier to review.
- **Protobuf** — considered, rejected because JSON is easier to inspect in an evidence pack.
- **CBOR + COSE for the whole platform** — worth exploring for evidence packs, but the API
  surface should stay JSON for developer ergonomics.
