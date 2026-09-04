# Security review (self-review)

> This is a **self-review**, performed by the same author as the code. It is not an
> independent audit and no certification is claimed. Its purpose is to make the platform's
> threat model explicit and to flag hardening steps that a real production deployment would
> require.

## In-scope

- The API surface (`src/AuditPlatform.Api`) and the append/query/verify/export flows.
- The persistence and interceptor code (`src/AuditPlatform.Infrastructure`).
- The integrity primitives (`src/AuditPlatform.Domain.Integrity`).

## Threat model (STRIDE)

### Spoofing

- **Threat**: an attacker forges JWTs claiming a target tenant or `audit:admin`.
- **Controls**: `Jwt:SigningKey` is validated at boot; Production refuses to boot with the
  dev-only value. Tokens are HS256 for the dev build.
- **Hardening**: switch to RS256 / EdDSA with a KMS-hosted private key and clients holding
  the public key.

### Tampering

- **Threat**: an attacker (or curious operator) mutates an audit event to hide evidence.
- **Controls**: the hash chain detects any single-event mutation and reports the sequence
  number precisely. Merkle checkpoints signed with an RSA key detect large-scale substitution.
  The append-only interceptor blocks in-process mutation.
- **Hardening**: DB role revokes `UPDATE`/`DELETE` on `AuditEvents`. Archive evidence packs to
  WORM storage. Rotate RSA signing key periodically and publish public keys with expiry.

### Repudiation

- **Threat**: an actor denies performing a recorded action.
- **Controls**: every event carries actor id, roles-at-time, correlation and causation ids,
  and source (ip, ua, service, region). The chain hash mathematically ties the event to a
  specific history; the checkpoint signature proves the platform-side commitment at a point
  in time.

### Information disclosure

- **Threat**: readers see `before/after` state they aren't cleared for.
- **Controls**: `RedactionService` removes sensitive fields based on reader clearance. The
  `ContentHash` remains so a knowledgeable reader (say, the original event issuer) can prove
  integrity without seeing redacted content.
- **Hardening**: encrypt `PayloadJson` at rest with per-tenant keys; log all reads through
  meta-audit (already implemented) and alert on abnormal access patterns.

### Denial of service

- **Threat**: ingest floods, expensive verifications.
- **Controls**: rate limiter is on by default; verification is O(n) with clear back-pressure.
- **Hardening**: per-tenant partitioned rate limits, a dedicated verification queue, and a
  hardware-attested checkpoint schedule.

### Elevation of privilege

- **Threat**: an authenticated but low-privileged user obtains `audit:admin` behaviour.
- **Controls**: five explicit scopes, each mapped to a policy. Endpoints declare their
  required scope. Meta-audit records every read so a scope escalation attempt is visible.
- **Hardening**: strict OAuth 2.0 with device posture checks, JIT elevation, and a break-glass
  workflow for admin actions.

## Sensitive data handling

- `PayloadJson` is stored verbatim after canonicalisation. Redaction happens at read time based
  on the reader's clearance; the underlying rows still contain the original bytes for
  verification.
- `IsTombstoned` truncates payload storage. Restoration of pruned data is impossible by design.
- No secrets or credentials are logged. `SecurityHeadersMiddleware` sets baseline headers.

## Dependencies

- Framework packages only (EF Core, JWT bearer, OpenTelemetry). No AGPL/GPL dependencies. All
  are Microsoft.* and OpenTelemetry.* — mature, audited providers.

## Findings

- **F1 (Low, dev-only)**: RSA signing key is generated per process and lost on restart. This
  is intentional for the dev build; production must persist and rotate the key via KMS/HSM.
- **F2 (Low)**: `DevTokenIssuer` is exposed on `/api/v1/dev/token` in Development only. Guarded
  by `IHostEnvironment.IsDevelopment()`; verified by test that Production refuses to boot with
  the dev-only signing key.
- **F3 (Low)**: OpenTelemetry currently exports to console. Production would wire a
  real backend (OTLP, Prometheus).

## Non-findings (things I checked)

- No SQL injection paths: EF Core parameterises everything; the two `ExecuteSqlRawAsync`
  usages (in tests) are parameterised.
- No path traversal: no file-system reads from user input.
- No unsafe deserialisation: System.Text.Json only, no polymorphic type resolution.

## Certification status

**No certification is claimed and no audit or assessment has been performed by any third
party.** All framings in `docs/compliance-notes.md` are descriptions of engineering controls,
not statements of legal compliance.
