# Compliance notes

> **No certification is claimed. No audit or assessment has been performed by any third party.**
>
> This document describes engineering controls implemented in this codebase and maps them to
> control themes that appear commonly in regulatory frameworks. It is **not** a compliance
> attestation, evidence of compliance, or a substitute for a real assessment by a qualified
> auditor. Language such as "supports" or "aligns with" describes engineering, not legal
> compliance.

## Access control

- JWT bearer authentication with strict issuer/audience/lifetime validation. HS256 for the dev
  build; RS256/EdDSA + KMS-hosted key recommended for production (see `security-review.md`).
- Five scoped policies: `audit:write`, `audit:read`, `audit:verify`, `audit:admin`, `audit:export`.
  Every endpoint declares its required scope explicitly.
- Tenant scoping is enforced at the query layer — every query and command derives the tenant
  id from the caller's token, never from user input.

## Integrity

- Per-tenant SHA-256 hash chain over deterministic canonical JSON. See `docs/integrity-model.md`.
- Periodic Merkle-tree checkpoints signed with an RSA key; single-event Merkle inclusion proofs
  supported.
- Three defence-in-depth layers of append-only enforcement (private setters, EF Core interceptor,
  DB-level revocation). See ADR-005.
- Chain verification detects payload tampering, deletion, and reordering, and identifies the
  exact sequence number where the mismatch was detected.

## Retention

- Per-tenant, per-category retention policies with a background pruner.
- Pruning tombstones the payload while preserving the hash chain — chain verification still
  passes across pruned events. See ADR-004.
- Legal hold on a `(resourceType, resourceId)` pair unconditionally blocks pruning of matching
  events; the pruner reports skips.
- Retention actions are themselves audited (`audit.retention.pruned`).

## Evidence collection

- Signed evidence packs bundle events + checkpoint roots + RSA signature + manifest with counts
  and hashes. Verify-a-pack endpoint round-trips.
- Streaming NDJSON/CSV exports so large ranges do not exhaust memory.
- Every read of the audit log is itself audited (meta-audit). The `IsMetaAuditor` flag prevents
  loops.

## Segregation of duties

- Scoped operations: an actor with `audit:write` cannot run retention (`audit:admin`), cannot
  export (`audit:export`), and cannot verify by another party's proof unless granted
  `audit:verify`.
- Meta-audit means an admin performing a broad read leaves a permanent record even if they
  intend to hide it — the record is immutable and linked into the same chain their read query
  touched.

## What is **not** claimed

- **Not** claimed: SOC 2 Type I or Type II compliance, PCI DSS scope reduction, HIPAA Business
  Associate readiness, ISO/IEC 27001 certification, GDPR "compliance", or any other regulatory
  certification, attestation, opinion, or legal status.
- **Not** claimed: fit-for-purpose in any specific regulated environment. Every regulated
  deployment requires a real gap analysis by a qualified assessor.
- **Not** claimed: correctness of the cryptographic construction beyond what unit and
  integration tests demonstrate. Cryptographic constructions should always be independently
  peer-reviewed before production use.

## Recommended next steps for any real regulated deployment

1. Independent cryptographic review of `HashChain`, `MerkleTree`, and `CanonicalJson`.
2. Independent security review with penetration testing.
3. KMS/HSM-hosted signing key with a rotation policy and public-key publication.
4. DB-level revocation of `UPDATE`/`DELETE` on `AuditEvents` and WORM storage for archived
   evidence packs.
5. Formal control mapping performed by a qualified assessor against the target framework(s).

**All claims here are engineering statements only. This project is a self-directed case study
built on a fictional tenant (Example Bank) and does not correspond to any real bank, customer,
or regulated environment.**
