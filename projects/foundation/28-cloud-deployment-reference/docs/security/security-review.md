# Security Review — Contoso Retail Storefront Cloud Deployment Reference

## Scope and method

Manual design/code review against the portfolio security standard and STRIDE. Scope includes the ASP.NET Core host, configuration bootstrap, database/outbox, deployment identity, Key Vault, Container Apps ingress, CI/CD and IaC. No dynamic cloud test was possible because no Azure resources were provisioned.

## Assets

- Product/order data and idempotency responses.
- Database credentials and JWT signing material.
- Managed identity tokens and RBAC assignments.
- Container images and release provenance.
- Outbox events, Service Bus queue and DLQ.
- Telemetry, correlation identifiers and operational logs.
- GitHub workflow authority and environment approvals.

## Trust boundaries

1. Internet client → Container Apps HTTPS ingress.
2. API/worker → database, Redis, Service Bus and Key Vault.
3. GitHub Actions → Entra OIDC → Azure control plane.
4. Migration job → production database.
5. Stable revision ↔ shared database ↔ candidate revision.

## Data classification

The repository contains fictional demo data only. In a real retail deployment, order identifiers and customer references are confidential business data; authentication material is secret; telemetry is internal and may contain identifiers. Payment-card or sensitive personal data is explicitly outside this reference.

## Threat model (STRIDE per boundary)

| Boundary | S | T | R | I | D | E |
|---|---|---|---|---|---|---|
| Client → API | JWT issuer/audience/signature and scope policy | TLS, edge validation, domain invariants | Correlation IDs and structured state-change logs | ProblemDetails avoids stack details; no secret output | Rate limit, bounded input, timeout | Policy-based scopes; no role string shortcuts |
| Workload → Key Vault | Managed identity | TLS and RBAC-controlled secret versions | Azure activity/data-plane logs required | Secret references; no values in app settings | Startup fails closed; bounded wait | Key Vault Secrets User at vault scope |
| Workload → DB/Redis/SB | Credentials/managed identity | TLS, DB constraints, outbox transaction, Service Bus duplicate ID | Order/outbox/checkpoint evidence | Private access option and no connection-string logs | Retries/circuit, readiness removal, queueing | Resource-scoped Service Bus roles |
| GitHub → Azure | OIDC subject/audience | Protected branches/environments and reviewed workflow | GitHub/Azure deployment logs | No Azure client secret | Environment gates and bounded rollout | Resource-group deployment role; custom role recommended |
| Migration job → DB | User-assigned identity/Key Vault secret | Committed ordered migrations | EF history and job execution ID | SecretRef only | One replica, timeout and retry limit | Separate job; database permission should be migration-specific |
| Stable/candidate → shared DB | Same approved workload identity | Expand/contract compatibility | Revision/resource telemetry | Common data access policy | Readiness and traffic rollback | Contract migration approval prevents old-code incompatibility |

## Mitigations implemented

- No real secret or personal data in source.
- `.env`, key/certificate files and databases are ignored.
- Production defaults guard rejects demo key and local adapters.
- Key Vault `IConfigurationSource` adapter supports managed identity.
- JWT bearer validation, policy scopes, 401 and 403 tests.
- RFC 7807 validation/errors with trace IDs.
- Explicit security headers and fixed-window rate limiting.
- Parameterized EF/SQL operations.
- Unique order idempotency key and transactional outbox.
- Outbox Service Bus `MessageId` equals persisted message ID.
- W3C/correlation propagation.
- OIDC deployment credentials and manual environment gates.
- Least-privilege workload role assignments.
- Canary rollback on missing/bad metrics.
- IaC tests reject literal secrets in environment files.

## Residual risk

- PostgreSQL password bootstrap remains; migrate to Entra database auth.
- The development HS256 token endpoint must remain disabled in production.
- Private DNS/endpoints and Azure RBAC were not deployment-tested.
- No WAF, DDoS plan, Defender configuration or image signing is deployed.
- Synchronous Key Vault bootstrap can make vault incidents startup-critical.
- Logs require production redaction rules and access control.
- Contributor is shown for the CD principal; a narrower custom role is preferable.
- No penetration, dependency supply-chain or container scanner was executed locally.

## What would change for a real production deployment

- Entra ID external issuer/JWKS and removal of local token issuing.
- PostgreSQL Entra token authentication and separate migration/runtime DB roles.
- Front Door Premium/WAF, validated private DNS, firewall deny rules and Azure Policy.
- Signed images, provenance/SBOM enforcement, Defender for Cloud and vulnerability gates.
- Central security monitoring, secret rotation automation and alert response.
- Data retention/privacy classification, access reviews and formal threat-model workshops.

## Explicit non-claims

This is a self-directed engineering demonstration. No formal security audit, penetration test, or compliance certification (PCI DSS, SOC 2, HIPAA, ISO 27001) has been performed or is claimed.
