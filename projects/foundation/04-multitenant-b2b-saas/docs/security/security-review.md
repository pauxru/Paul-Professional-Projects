# Security Review — FieldOps Multi-Tenant B2B SaaS

## Scope and method

This review covers the repository's trust boundaries, tenant isolation, authorization, webhook ingestion, local file storage, audit evidence and default configuration. It uses STRIDE as a design review, supported by code inspection and automated tests. It is not a penetration test.

## Assets

- Tenant-confidential assets, jobs, schedules, inspections and attachments.
- Membership/role assignments and invitation tokens.
- Organization subscription state, entitlements and usage.
- Feature rollout configuration and user overrides.
- Billing webhook secret and event receipts.
- Audit evidence, correlation IDs and before/after hashes.
- Platform-administrator access to unfiltered views.

## Trust boundaries

1. Untrusted browser/API client to ASP.NET Core.
2. Authenticated identity to selected organization.
3. Tenant-scoped application code to shared database/cache.
4. Platform administrator to `IgnoreQueryFilters` data.
5. Billing sender to anonymous webhook endpoint.
6. API to local filesystem object store.
7. Configuration/environment to cryptographic key consumers.

## Data classification

| Data | Classification | Handling |
|---|---|---|
| Synthetic tenant operational records | Confidential by tenant | query filters, write interception, RBAC |
| JWT and webhook secrets | Secret | configuration only; demo placeholders; never logged |
| Invitation raw token | Secret, short-lived | returned once; only SHA-256 hash stored |
| Attachment content | Confidential by tenant | allow-list, size cap, namespaced path outside web root |
| Audit log | Security-sensitive | insert-only, tenant filter, permission-gated |
| Demo names/emails | Synthetic | explicitly fictional/non-personal |

## Threat model (STRIDE per boundary)

| Boundary | S | T | R | I | D | E |
|---|---|---|---|---|---|---|
| Client -> API | forged JWT | request/body/header manipulation | denied action disputed | IDOR / verbose errors | request floods / oversized upload | stale role claim or policy bypass |
| Identity -> tenant | spoofed tenant header | conflict JWT/header/subdomain | organization switch disputed | cross-tenant read/list | tenant-targeted flood | claim selects unauthorized tenant |
| App -> shared DB/cache | forged ambient context | discriminator mutation | missing mutation evidence | forgotten predicate/cache collision | unbounded query | `IgnoreQueryFilters` misuse |
| Platform admin -> unfiltered data | stolen admin token | unfiltered mutation | admin read disputed | all-tenant disclosure | expensive platform query | ordinary owner reaches admin route |
| Billing -> webhook | fake sender | raw-body/signature tampering | sender denies event | error/log leakage | replay flood | forged success/failure changes tenant state |
| API -> filesystem | malicious filename/key | overwrite/traversal | upload disputed | foreign object read | very large file / disk exhaustion | arbitrary file access |
| Config -> crypto | default/weak key | secret replacement | unknown key operator | key logged/committed | missing configuration prevents boot | production accepts demo key |

## Mitigations implemented

### Tenant isolation and IDOR

- Configurable JWT, `X-Tenant` and subdomain strategies all resolve to organization IDs.
- Different non-empty sources return 400 rather than silently choosing one.
- Active membership is verified before tenant routes; suspended tenants return 403.
- Every EF entity implementing `ITenantOwned` has a global query filter.
- The SaveChanges interceptor stamps empty insert IDs and compares both original/current IDs on all writes.
- Cross-aggregate services use `TenantGuard`.
- Cross-tenant read/update/list tests prove foreign resources become 404 or remain absent.
- A test deliberately uses `IgnoreQueryFilters` to load a foreign job and proves SaveChanges rejects it.
- Admin unfiltered endpoints require the `platform-admin` claim; ordinary owners receive 403.

### Authentication and authorization

- JWT issuer, audience, signature, expiry and key are validated.
- Production startup rejects the development key prefix.
- Permissions are policies backed by current membership role, not only token role claims.
- Role changes invalidate the tenant-scoped permission cache immediately.
- Tenant request and platform-admin policies are distinct.

### Webhooks

- HMAC-SHA256 covers the timestamp plus exact raw body.
- The timestamp has a configurable tolerance.
- `CryptographicOperations.FixedTimeEquals` compares signatures.
- Malformed hex/header values fail closed.
- Event ID is a unique receipt; duplicate delivery performs no second state transition.
- Tests cover valid, invalid, stale, replayed, failure escalation and successful reactivation.

### Input, files and abuse

- Minimal API DTO binding plus validation/domain invariants.
- Page size is capped at 100.
- ASP.NET Core fixed-window rate limiting is partitioned and a plan-aware minute gate applies entitlement limits.
- Attachment content types are restricted to JPEG, PNG and PDF; length is capped at 10 MiB.
- File names are reduced to `Path.GetFileName`, paths are normalized and object keys contain tenant ID.
- CORS uses configured origins; security headers include CSP, frame denial, nosniff, referrer and permissions policy.

### Audit and observability

- Audit rows include tenant, actor, action, resource, before/after SHA-256 hashes, time, correlation, IP and user agent.
- `IAppendOnly` changes/deletes are rejected by the interceptor.
- Security denials are structured log events and metrics.
- Response errors avoid returning exception details for unexpected server failures.

## Residual risk

- Raw SQL added outside reviewed repositories could bypass EF controls; production should add database row-level security.
- HS256 demo tokens lack OIDC key rotation/revocation.
- A compromised platform-admin token has broad read access.
- The local cache, rate meter and quota lock are process-local and unsuitable for independent replicas.
- Invitation hashes are unsalted SHA-256; the generated 256-bit random token makes offline guessing impractical, but a keyed hash would improve compartmentalization.
- Local object files have no malware scan, encryption-at-rest control, object lock or lifecycle policy.
- Webhook event ordering beyond duplicate ID is not modeled.
- Audit hashes are not chained or anchored externally.
- CSP permits inline styles for the small dashboard.

## What would change for a real production deployment

- Federated OIDC with asymmetric keys, short tokens, revocation and conditional access.
- Managed PostgreSQL/SQL Server with row-level security and least-privilege database identities.
- Distributed Redis/database atomic quotas, cache invalidation and rate limits.
- Cloud blob storage with encryption, malware scanning, signed downloads and retention.
- Managed secret store, key rotation and dual-secret webhook verification.
- WAF, TLS-only transport, private database endpoints and centralized SIEM/OTLP.
- Time-boxed/approved platform administration and detailed admin-access audit.
- Outbox/reconciliation for billing, ordering constraints and incident alerting.
- Independent security review, SAST/dependency scanning and penetration testing.

## Explicit non-claims

This is a self-directed engineering demonstration. No formal security audit, penetration test, or compliance certification (PCI DSS, SOC 2, HIPAA, ISO 27001) has been performed or is claimed.
