# Security Review — Collab Real-Time Collaboration Backend

> Scope: the application as built in this repository. This is a **self-directed engineering case
> study**, not a production security certification. The "Explicit non-claims" section states plainly
> what has **not** been done, so nothing here is mistaken for a guarantee.

## 1. Assets and trust boundaries

| Asset | Why it matters |
|---|---|
| Document content & operation log | The product's data; integrity and confidentiality both matter |
| Workspace membership & roles | Authorization decisions derive from these |
| JWTs | Bearer credentials; compromise = impersonation |
| Audit records | Forensic truth; must be tamper-evident (append-only) |
| Notifications inbox | Can leak activity/mentions across users if mis-scoped |

**Trust boundaries:** browser ⇄ API/hub (untrusted client → trusted server); API ⇄ SQLite (in-proc);
API ⇄ (future) Redis backplane (untrusted network — unverified, see ADR-005).

The **server is authoritative** for ordering, persistence, and *every* authorization decision. No
client assertion (role, membership, user id in a body) is trusted; identity comes only from the
validated JWT subject, and capability comes only from `RolePolicy` evaluated server-side.

## 2. STRIDE analysis

| # | Threat (STRIDE) | Vector in this system | Mitigation implemented | Residual risk |
|---|---|---|---|---|
| S1 | **Spoofing** | Forged/edited JWT to impersonate a user | JWT signature validated (HMAC-SHA256), issuer/audience/lifetime validated, `sub` → user id; hub `[Authorize]`; browser token passed via `access_token` query only on the hub path | Symmetric dev key in config — see non-claims; no key rotation |
| S2 | **Spoofing (transport)** | Man-in-the-middle on WebSocket | TLS expected at the edge in production | HTTP-only on the local demo host by design (no `UseHttpsRedirection`); TLS is a deployment concern |
| T1 | **Tampering (content)** | Client submits ops to corrupt a document or others' state | Server validates causal readiness; unknown references → `Rejected{unknown_reference}`; RGA merge is intention-preserving; sequence assignment is server-side and gap-free | CRDT interleaving is inherent (documented), not a security flaw |
| T2 | **Tampering (history)** | Rewriting past versions | Operation log and audit records are **append-only**; restore is a **forward** operation, never a rewrite; unique `(DocumentId, ServerSequence)` index | An attacker with direct DB write access bypasses app controls (out of scope) |
| R1 | **Repudiation** | "I didn't change that" | Append-only `audit_records` capture actor, action, resource, correlation id, timestamp; operation log stores author user + replica per change | Audit not cryptographically signed (see non-claims) |
| I1 | **Information disclosure (IDOR)** | Reading/writing another workspace's document by guessing a GUID | Every REST and hub call resolves membership and evaluates `RolePolicy`; non-members are denied `View`; tested (`RestApiTests` 403, `HubAuthorizationTests` non-member join denied) | Relies on correct policy checks at every new endpoint — enforced via a single `RolePolicy` chokepoint |
| I2 | **Information disclosure (notifications)** | Reading another user's inbox | Notifications are queried by the authenticated `sub` only | — |
| I3 | **Information disclosure (history exfiltration)** | Pulling full document history to exfiltrate data | History/time-travel/diff endpoints require `View` on the document; same membership gate as content | A legitimate `Viewer` can read all history by design; least-privilege is role-based, not per-line |
| D1 | **DoS (operation flooding)** | A client spams `SubmitOperation` to overload the server | Per-connection **token-bucket** rate limiter (sustained `OperationsPerSecondPerConnection`, burst `OperationBurst`); repeated violations → forced disconnect (`MaxViolationsBeforeDisconnect`); tested (`HubRateLimitTests`) | Per-node only (see ADR-005); no global cross-node cap |
| D2 | **DoS (oversized payloads)** | Huge operation batches or documents exhaust memory | `MaxOperationsPerSubmit` and `MaxDocumentLength` reject oversized submissions; REST global fixed-window limiter (1000/min per subject/IP) | Very large *reads* (history of a huge doc) not separately capped beyond paging |
| D3 | **DoS (connection storm)** | Mass reconnect flood | See `docs/runbooks/hub-connection-storm.md`; hub is excluded from the REST limiter but has per-connection limits | Connection-count limiting is a deployment/edge concern (not in-proc) |
| E1 | **Elevation of privilege** | A `Viewer`/`Commenter` performing edits or management | Capability checks via `RolePolicy` on every mutating path: `Edit` for operations, `Comment` for comments, `Manage` for membership/role changes; tested (viewer edit denied) | Role assignment itself is an `Owner` capability; a compromised Owner is a trusted-insider risk |
| E2 | **Elevation via mass assignment** | Client sends `role`/`userId` in a body to escalate | Server ignores client-asserted identity; role changes require `Manage` and are audited | — |

## 3. Cross-cutting protections

- **XSS in rendered content.** The web client renders document text into a `<textarea>` via the
  `value` property and builds presence rows with `textContent` — **never** `innerHTML` — so document
  or user-supplied text cannot inject markup or script. Any future HTML/rich rendering **must**
  sanitize/encode; this is called out as a hard requirement for that work.
- **ProblemDetails everywhere.** Errors return RFC-7807 `application/problem+json` with correlation
  ids and without leaking stack traces (detailed errors are enabled only in Development/Testing).
- **Correlation ids.** Every request/response and audit record carries a correlation id for tracing
  and incident forensics.
- **Input validation.** Data-annotation validation on requests (e.g. email format, title required,
  known document type) returns `400` with a ProblemDetails body; tested.
- **CORS.** Permissive for the local demo (`SetIsOriginAllowed(_ => true)` + credentials) — this is a
  **demo** setting and is flagged in non-claims; production must pin an allow-list.
- **Secrets hygiene.** Only `.env.example` is committed; `.env`, `*.db`, and local overrides are
  gitignored. No real secrets in the repo.

## 4. Explicit non-claims (read this)

This system has **not** undergone and does **not** claim:

- Any third-party penetration test, threat-model sign-off, or security certification.
- Production-grade auth: the token endpoint is **password-less by design** (email → JWT) to keep the
  realtime demo friction-free. It is **demo-grade** and must be replaced with a real IdP/OIDC and
  refresh-token handling before any real use.
- Key management: the JWT signing key is a **symmetric dev key in configuration**. There is startup
  refusal to run in Production with the default key, but there is **no** rotation, KMS, or asymmetric
  signing.
- Transport security: TLS is assumed to be terminated at the edge; the app host itself serves HTTP.
- Tamper-evident audit: audit/log rows are append-only at the **application** layer but are **not**
  cryptographically chained or signed; someone with direct database access is out of scope.
- Multi-tenant isolation beyond workspace membership checks; no per-row encryption; no DLP.
- Rate limiting across multiple nodes (per-node only; see ADR-005).
- CORS lock-down, security-header hardening beyond the basics, or WAF/edge protections.

## 5. Recommended hardening before production

1. Replace demo token issuance with OIDC/OAuth2 + short-lived access tokens and refresh rotation.
2. Move JWT signing to asymmetric keys with rotation via a secrets manager/KMS.
3. Pin CORS to known origins; add a full security-header set and TLS everywhere.
4. Add cross-node rate/connection limits and move presence to shared state (ADR-005) before scaling.
5. Consider cryptographic chaining of the audit log for tamper evidence.
6. Add per-user/per-workspace quotas and abuse analytics on top of the per-connection limiter.
