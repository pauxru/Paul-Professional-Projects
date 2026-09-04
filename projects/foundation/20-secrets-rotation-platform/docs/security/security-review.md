# Security Review — Northstar Secrets Rotation Platform

## Scope and method

This review covers the code and local deployment model in this repository. It uses architecture
review, data-flow analysis, abuse-case analysis, and STRIDE across the HTTP, application,
persistence, notification, and key-provider boundaries. It does not cover an actual cloud tenant,
network perimeter, host hardening, or operational personnel.

## Assets

- Plaintext secret values during short-lived generation, verification, and retrieval.
- Per-version data-encryption keys and versioned wrapping keys.
- Ciphertext, authentication tags, nonces, and AAD identity metadata.
- Consumer identities, subscriptions, and acknowledgement state.
- Rotation state, idempotency keys, approval state, policies, and append-only audit evidence.
- JWT signing material and webhook signing material.

## Trust boundaries

1. Operator or consumer to ASP.NET Core over HTTP.
2. JWT validation and scope authorization to application use cases.
3. Path-policy decision to value decryption.
4. Application process to SQLite.
5. Envelope cipher to `IKeyProvider`.
6. Notification channel to consumer endpoint.
7. Local adapters to future cloud SDK adapters.

## Data classification

| Data | Classification | Handling |
|---|---|---|
| Plaintext values/private keys | Restricted secret | Memory only, never logged, `no-store` response |
| Master/wrapping key | Restricted cryptographic | Environment input; fake Development default only |
| Wrapped DEK/ciphertext/tag/nonce | Confidential | SQLite; still protected by filesystem controls |
| Secret paths/owners/consumers | Internal | Authenticated metadata API |
| Audit records | Security-sensitive | Append-only application path, correlation IDs |
| Synthetic seed identities | Public demo | Explicitly fictional |

## Threat model (STRIDE per boundary)

| Boundary | STRIDE | Threat | Implemented mitigation | Residual risk |
|---|---|---|---|---|
| Client → API | S/E | Forged identity or excessive privilege | JWT signature, issuer/audience/lifetime, scope policies | Local HS256 issuer is demonstration-only |
| Client → value API | I/D | Secret scraping or brute-force reads | Distinct read scope, path RBAC, required reason, fixed-window rate limit, audit | Distributed attackers need shared gateway controls |
| API → application | T/R | Altered request or denied action later disputed | DTO validation, domain invariants, actor/reason/correlation audit | Host compromise can alter process behavior |
| Application → SQLite | T | Ciphertext transplant between rows | AAD binds ID, path, type, version; tested fail-closed | Database rollback attacks require external integrity/backup controls |
| Cipher → key provider | I/E | Master-key compromise increases blast radius | Unique DEK per version, wrapping-key versions, re-wrap path, Production guard | Local provider keeps historical keys in process memory |
| Rotation → consumer | S/T | Spoofed notification triggers malicious refresh | HMAC-SHA256 timestamp/body signature, constant-time comparison, references only | Demo transport lacks persisted nonce replay defense |
| Consumer → rotation | S/R | False acknowledgement | JWT scope, consumer-specific rotation membership, timestamped persisted ack | Production should bind consumer IDs to workload identity claims |
| Scheduler → rotation | D | Rotation storm at common deadline | Deterministic ±10% jitter, active-operation check, idempotency | Single-node scheduler has no distributed lease |
| Operator → break glass | E/R | Insider bypass or destructive abuse | Expiring four-eyes approval, different approver, single-use execution, audit | Collusion and compromised approver accounts remain possible |
| Logging pipeline | I | Plaintext appears in log state/exception | Registered-value redaction provider, structured logging discipline, leak-scanning test | Memory dumps and third-party profilers are outside this control |

### Priority abuse cases

**Insider read abuse.** Metadata and value scopes are separate; path policies narrow the permitted
hierarchy; every allowed and denied read records actor, stated reason, source, and correlation ID.
Reports identify first-time readers, readers with no baseline, spikes, and reads outside 08:00–18:00
UTC. Production should add just-in-time access, immutable remote audit export, alerting, and
manager/incident linkage.

**Ciphertext transplant.** Moving valid ciphertext, nonce, tag, and wrapped DEK to a different
secret does not decrypt because the secret ID, canonical path, type, and version are authenticated
as AAD. The suite explicitly swaps bindings and expects authentication failure.

**Key compromise blast radius.** Each version has a unique DEK, so compromise of one unwrapped DEK
does not expose other rows. Compromise of the local master key can expose every DEK wrapped by that
version. Production must use a managed HSM/KMS, least-privilege unwrap permission, key-use logs,
rotation, and revocation.

**Rotation-induced outage.** Dual-write retains the old version while consumers refresh and
acknowledge. Verification precedes actual promotion. Timeout or verification failure revokes the
candidate and preserves the known-good version. Single-cutover requires an explicit maintenance
window and is documented as higher risk.

**Notification spoofing.** Webhook messages are signed over timestamp plus exact body with
HMAC-SHA256, compared in constant time, and contain a reference rather than a value. Production
requires a timestamp acceptance window, durable nonce/replay storage, per-consumer keys, TLS
validation, and ideally mTLS.

## Mitigations implemented

- AES-256-GCM with 96-bit nonces, 128-bit tags, fresh 256-bit DEKs, and canonical AAD.
- Versioned wrapping keys and ciphertext-preserving master-key rotation.
- JWT bearer validation, policy authorization, path-glob RBAC, and value-read rate limiting.
- Required reason and correlation ID for every value-read attempt.
- Redacting logging provider and tests that scan captured output.
- HMAC webhook signatures, bounded retry, dead-letter records, and alternate channels.
- Persisted state machine, idempotency key, acknowledgement timeout, verification, rollback.
- Expiring, single-use, different-actor approval for destructive operations.
- Production startup rejection of the development JWT/master key.
- `.gitignore` for secret-bearing files and only obvious placeholders in tracked configuration.

## Residual risk

- SQLite file compromise exposes metadata and ciphertext for offline attack.
- Process compromise can observe plaintext and local wrapping keys in memory.
- In-memory notification/DLQ adapters lose state on restart.
- The local token endpoint is inappropriate outside Development/Testing.
- The sample policy engine has no deny rule, delegated administration, or external policy source.
- Audit immutability is enforced by application behavior rather than WORM storage.
- No malware scanning, host attestation, HSM, mTLS, or secret-memory pinning is implemented.

## What would change for a real production deployment

Use workload identity and OIDC; managed HSM/KMS wrapping; per-environment isolation; server-grade
database with migrations, encryption and backups; durable outbox and DLQ; distributed leases;
gateway rate limiting; immutable SIEM export; per-consumer webhook keys with replay storage;
network allow-lists/private endpoints; just-in-time approval; incident integration; periodic
cryptographic review; dependency scanning; SAST/DAST; and formal restore/rotation game days.

## Explicit non-claims

This is a self-directed engineering demonstration. No formal security audit, penetration test, or
compliance certification (PCI DSS, SOC 2, HIPAA, ISO 27001) has been performed or is claimed.

No Azure resources were provisioned. Docker configuration was authored but not run on the build
host. The system has not handled real credentials, production traffic, or real customer data.
