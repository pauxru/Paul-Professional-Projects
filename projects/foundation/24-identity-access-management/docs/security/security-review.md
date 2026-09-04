# Security Review — Northstar Identity Governance Administration

## Scope and method

This review covers the repository's trust boundaries, JWT-protected administration API, custom authorization engine, lifecycle/approval/certification workflows, provisioning simulators, SQLite persistence, browser administration UI, and background workers. The method is design review plus STRIDE analysis and verification against automated tests. It is not a penetration test.

## Assets

- Authoritative identity and employment attributes.
- Role, group, entitlement, policy, and SoD configuration.
- Approval and exception authority.
- Effective access and time-bound elevation.
- Connector desired/actual state and quarantine records.
- Certification decisions and revocation exclusions.
- Append-only audit history and hash links.
- JWT signing material and OIDC configuration in a production deployment.

## Trust boundaries

1. Administrator/browser to ASP.NET Core API.
2. API authentication/authorization middleware to governance application services.
3. Application/domain logic to SQLite.
4. Governance service to HR and target connectors.
5. Background workers to state-changing governance operations.
6. Local development token issuer versus a production OIDC provider.

## Data classification

The repository contains synthetic fictional data only. A real deployment would treat identity attributes, manager relationships, access profiles, approval evidence, IP/device context, and audit events as confidential security data. Ticket references and recording links may also reveal incident information. Secrets and signing keys are restricted data and must come from a managed secret store.

## Threat model (STRIDE per boundary)

| Boundary | S | T | R | I | D | E | Implemented controls |
|---|---|---|---|---|---|---|---|
| Client → API | stolen/forged JWT | body/parameter manipulation | denied approval action | access-profile leakage | request flooding | admin/approver scope abuse | issuer/audience/signature/lifetime validation, scope policies, approver-to-subject binding, validation, correlation IDs, rate limiting, ProblemDetails |
| Middleware → service | claim confusion | actor ID spoofing | missing attribution | over-broad response | expensive estate queries | approval bypass | explicit policies, command actor equality for decisions, actor/correlation audit, bounded pagination |
| Service → SQLite | process identity misuse | direct row/update tampering | audit deletion | database file disclosure | lock/large query pressure | forged role/policy edge | parameterized EF, unique/FK constraints, DAG cycle checks, append-only guard, hash-linked audit |
| IGA → connectors | connector impersonation | target response manipulation | target denies change | account/permission disclosure | connector outage | rogue target grant | port boundary, deterministic adapter, retry/quarantine, independent reconciliation |
| Workers → service | fake worker identity | premature expiry/revocation | untraceable automation | logs expose details | runaway loop | worker performs admin action | fixed system actors, correlation IDs, bounded periodic execution, same domain checks/audit |
| Local token → production | dev issuer used as trusted IdP | default key retained | weak actor evidence | token leakage | token minting flood | arbitrary scope minting | endpoint disabled outside Development/Testing, production default-key startup refusal, known-scope allow-list |

## Mitigations implemented

### Privilege escalation paths

- Protected endpoints require `iga.read`, `iga.admin`, or `iga.approve`; administration is not inferred from a display role.
- Approval, rejection, delegation, elevation approval, and certification compare the command's actor/reviewer GUID with the authenticated JWT subject.
- JIT works only for catalogue entries explicitly marked privileged and expires both by worker state transition and resolver-side end-time checks.
- Role-edge creation runs transitive cycle detection before persistence.
- Terminated and suspended identities are denied before policies can grant access.

### Approval bypass

- Request stages are persisted and the state machine only accepts the current pending approver.
- Parallel owner stages require every step before advancement.
- Delegation changes the current approver and preserves `OriginalApproverId` plus `WasDelegated`.
- SLA escalation marks the step and routes it to the configured security approver.
- Rejection requires a non-empty reason.
- Low-risk auto-approval is explicit and risk-derived, not a client flag.

### SoD evasion

- The preventive check resolves the complete requested role/group hierarchy, not only the top-level target.
- The detective scan resolves all current direct, role, group, and JIT paths.
- Exceptions require an approving authority, justification, and future expiry; expired exceptions do not suppress controls.
- Reversing the order of toxic grants still produces the same set-based detection.

### Orphan accounts and rogue grants

- Reconciliation reads independent connector state.
- Missing/terminated source identities with enabled target accounts are classified as orphans.
- Permissions absent from effective IGA derivations are classified as rogue target-side grants.
- Forward failures persist attempts and move exhausted jobs to quarantine.
- The leaver workflow revokes internal access before connector disable, terminates sessions, then runs reconciliation.

### Audit integrity

- EF Core rejects modified or deleted `AuditRecord` entities.
- Records contain actor, action, resource, timestamp, correlation ID, before/after hashes, previous hash, and record hash.
- Security-sensitive grant, revoke, policy decision, approval, lifecycle, campaign, elevation, and provisioning actions append evidence.
- Production should externally anchor hashes; the local database file alone is not tamper-proof against an operating-system administrator.

### Policy-engine denial of service

- Permission wildcard matching and JSON conditions are constrained; policies cannot execute arbitrary code.
- API rate limiting and page-size caps reduce trivial abuse.
- Role cycles are rejected, preventing unbounded graph traversal.
- Conditions are AND-only and deterministic.
- Residual risk remains for synchronous estate-wide policy simulation and campaign generation. Production should queue, partition, cap policy/condition size, cache access graphs, and impose execution budgets.

### Browser and transport

- CSP, `X-Frame-Options: DENY`, `nosniff`, `Referrer-Policy`, and `Permissions-Policy` are applied.
- HSTS is emitted on HTTPS.
- CORS uses a configured origin allow-list.
- HTTPS termination is expected in production; local development uses HTTP on port 5024.

## Residual risk

- The development token endpoint can mint known scopes and must never be enabled in production.
- HS256 local signing is not a production key-management solution.
- A database/host administrator can replace the SQLite file and recompute local hashes.
- Simulation and detective scans use synchronous full-estate evaluation.
- Connector simulators do not model vendor throttling, eventual consistency, or credential rotation.
- Policy authors with administration scope can create broad allows or denies; production needs policy change approval, versioning, staged rollout, and blast-radius limits.
- The system does not implement step-up authentication itself; it consumes MFA level as decision context.
- Session recording metadata is a reference, not cryptographic proof that recording occurred.

## What would change for a real production deployment

- Validate OIDC tokens through provider JWKS, issuer allow-lists, key rotation, conditional access, and workload identities.
- Store secrets in Key Vault/HSM-backed services and use asymmetric signing where this service issues any token.
- Use SQL Server/PostgreSQL with migrations, optimistic concurrency, encrypted storage, backups, and row-level operational access controls.
- Dispatch provisioning through an outbox/queue with idempotency keys, leases, backoff/jitter, and connector-specific least-privilege credentials.
- Export audit to immutable WORM/object-lock storage and regularly verify/anchor the hash chain.
- Add policy change approval, peer review, shadow evaluation, maximum simulation cost, and emergency deny rollback.
- Add SAST, dependency scanning, DAST, browser testing, security logging/SIEM alerts, and formal incident exercises.

## Explicit non-claims

This is a self-directed engineering demonstration. No formal security audit, penetration test, or compliance certification (PCI DSS, SOC 2, HIPAA, ISO 27001) has been performed or is claimed.
