# Security Review — Example Bank Digital Banking Ledger

> **Scope and honesty statement.** This is a self-directed engineering case study, not a
> penetration-tested, audited, or production-deployed system. This document records the security
> posture that *is* implemented, the threats considered, and — explicitly — what is **not** claimed.
> No real customer data, secrets, or funds are involved.

## Assets to protect

1. **Financial integrity** — the invariant that money can be neither created nor destroyed.
2. **The audit trail** — the append-only, hash-chained history of entries.
3. **Authorization boundaries** — who may read, post, adjust, or administer the ledger.
4. **Availability of correctness** — the system should fail closed (reject) rather than post a wrong
   entry.

## Controls implemented

### Financial integrity
- Balanced-entry invariant enforced in the domain (`UnbalancedEntryException`), per currency.
- Append-only enforced in `LedgerDbContext.SaveChanges` (`AppendOnlyViolationException` on any
  update/delete of a persisted entry or posting).
- Corrections only via **reversal** entries that reference the original and cannot exceed it.
- Cached balances reconciled against derived balances by `IntegrityService`; a mismatch is reported,
  not hidden.
- Concurrency protocol (ordered locking + optimistic versions + serialized append + idempotency)
  prevents overdraw / lost updates — see `docs/concurrency-notes.md`.
- Money is exact integer minor units; no floating point.

### Privilege separation
- JWT bearer authentication (issuer, audience, lifetime, signing key all validated; 30 s clock skew).
- Four **least-privilege scope policies**, enforced per endpoint:

  | Scope | Capability | Example endpoints |
  |-------|-----------|-------------------|
  | `ledger:read` | read only | balances, statements, trial balance, FX quote |
  | `ledger:post` | move money within rules | entries, transfers, holds, FX convert |
  | `ledger:adjust` | corrections & status | reversals, freeze/activate |
  | `ledger:admin` | governance | create/close accounts, integrity verification |

  Posting money (`ledger:post`) is separated from correcting it (`ledger:adjust`) and from
  administering accounts (`ledger:admin`), so a compromised posting credential cannot reverse entries
  or open/close accounts.

### Audit & tamper evidence
- SHA-256 hash chain over all entries; verification endpoint detects any retroactive edit or
  reordering and reports the first broken sequence (ADR-004).
- Every entry records source system, correlation id, value date, and booking timestamp.

### Transport & error hygiene
- All errors are RFC 7807 `ProblemDetails` with a stable `code`; no stack traces or internal detail
  leak to clients.
- Correlation ids on every request for traceable auditing.
- Idempotency keys prevent duplicate-submission money movement.

## STRIDE analysis

| Threat | Vector | Mitigation | Residual risk |
|--------|--------|-----------|---------------|
| **S**poofing | Forged / missing JWT | Signed JWT with validated issuer/audience/lifetime/key; anonymous only on `/health` and (dev-only) token mint | Single symmetric key in demo; no IdP/MFA (see non-claims) |
| **T**ampering | Editing entries or DB rows | Append-only guard + SHA-256 hash chain + reconciliation self-check | An attacker with direct DB write could recompute the chain forward (no external anchor yet) |
| **R**epudiation | "I didn't post that" | Correlation ids, source system, idempotency keys, immutable hash-chained entries | No per-user signing / non-repudiation beyond JWT subject |
| **I**nformation disclosure | Leaking balances/PII in errors | ProblemDetails only; least-privilege read scope; no secrets in responses | No field-level encryption; demo data only |
| **D**enial of service | Flooding write endpoints | Bounded retry; fail-closed validation; stateless auth | **No rate limiting configured**; global chain lock is a throughput bottleneck |
| **E**levation of privilege | Using a read token to post/adjust | Per-endpoint scope policies; posting/adjusting/admin separated | Coarse scopes; no per-account ACLs; no four-eyes (see below) |

## Four-eyes / maker-checker (gap analysis)

High-risk operations in real banking (adjustments, reversals, account closure) are typically subject
to **dual control**: one user proposes, a different user approves. This project enforces the
*privilege* to adjust (`ledger:adjust`) and to administer (`ledger:admin`) but **does not implement a
maker-checker workflow** — a single holder of the scope can both propose and commit. Adding it would
mean: a pending-adjustment state, a distinct approver identity check (approver ≠ maker), and an
audit record of both parties. This is documented as intended future work, not as an implemented
control.

## Explicit non-claims (what this project does NOT provide)

- ❌ Not penetration-tested, threat-modelled by a third party, or security-audited.
- ❌ No production deployment, real users, real funds, or uptime/SLA claims.
- ❌ **No rate limiting** is configured on the API.
- ❌ **No four-eyes / maker-checker** approval workflow (privilege scopes only).
- ❌ Symmetric HS256 signing with a single well-known **dev key**; no IdP/OIDC, key rotation, MFA, or
  asymmetric keys. The default key is safe only for local use and must be overridden.
- ❌ The hash chain proves *internal* consistency only; there is **no external notarisation**, so it
  is tamper-**evident**, not tamper-**proof** against an attacker with database write access.
- ❌ No field-level encryption, PII handling, or data-residency controls (demo data is synthetic).
- ❌ In-process locks are single-node; multi-node safety requires the Postgres path (ADR-005).
- ❌ The dev token-minting endpoint (`/api/v1/dev/token`) must never be enabled outside
  Development/Testing.

## Recommendations (priority order)

1. Replace the symmetric dev key with an OIDC/JWKS provider and rotate signing keys.
2. Add rate limiting (built into ASP.NET Core) on mutating endpoints.
3. Implement maker-checker for `ledger:adjust` / account closure.
4. Externally anchor the hash-chain head (periodic notarisation) for stronger tamper resistance.
5. Add per-account authorization (not just coarse scopes) for multi-tenant use.
