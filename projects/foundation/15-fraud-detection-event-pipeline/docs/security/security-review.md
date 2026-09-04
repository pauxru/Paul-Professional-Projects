# Security Review — Real-Time Fraud Detection Event Pipeline

## Scope and method

This review covers the codebase in this repository as of the commit that ships with it. It uses the
STRIDE threat modelling shorthand across the trust boundaries described below. It is **not** a
production security assessment; it is the self-review a competent engineer would do before asking
a security team for a formal review.

The system was reviewed by reading the code (starting from `Program.cs`), tracing every request-
handling path, and threat-modelling the ingestion / scoring / case-management flows.

## Assets

| Asset | Sensitivity |
| --- | --- |
| Scoring decisions and reason codes | High — reversing a decline requires audit |
| Ruleset definitions | High — tampering changes every future decision |
| Case investigation notes | Medium — may contain analyst hypotheses |
| Analyst identities and roles | Medium — abuse of privilege is a real threat |
| Transaction event log | Medium — replayable state; PII already minimised |
| JWT signing key | Critical — impersonation risk |

**Explicit non-assets** in this repo:
- No real cardholder PAN, CVV, expiry, or holder name. The `CardId` field is a synthetic reference
  to a fictional card.
- No real customer PII. `CustomerId` is a synthetic reference.
- No real IP addresses. Synthetic IPs are drawn from RFC 5737 documentation ranges.

## Trust boundaries

```
[ Merchant terminal / caller ]   --HTTPS + JWT-->   [ API :5015 ]
[ Analyst UI ]                   --HTTPS + JWT-->   [ API :5015 ]
[ API process ]                  <--in-process-->   [ FeatureStore, RuleEngine, ScoringService ]
[ API process ]                  --EF Core-->       [ SQLite file / :memory: ]
[ API process ]                  --Console-->       [ OpenTelemetry exporter ]
```

## Data classification

| Field | Class | Rationale |
| --- | --- | --- |
| `TransactionRef` | Internal | Merchant-scoped, not sensitive alone. |
| `CardId` | Pseudonymous synthetic | No PAN mapping in this repo. |
| `CustomerId` | Pseudonymous synthetic | No customer PII in this repo. |
| `Amount`, `Currency` | Internal | Business data, not PII. |
| `IpAddress` | Pseudonymous | Real deployments would treat this as PII under GDPR / DPA-2019. |
| `GeoLocation` | Pseudonymous | Same. |
| Case notes | Confidential | Free text — must not be exposed on client-facing endpoints. |
| JWT signing key | Secret | Must never enter the repo. |

## Threat model (STRIDE per boundary)

### Caller ↔ API

| Letter | Threat | Mitigation in this repo | Residual |
| --- | --- | --- | --- |
| S | Impersonating a caller | JWT with HS256 signature; `ValidateIssuer`, `ValidateAudience`, `ValidateLifetime`, `ClockSkew=1min`. `DevTokenIssuer` in dev; production expected to swap the `ITokenIssuer`. | JWT key rotation is out of scope for this repo. |
| T | Modifying request in flight | HTTPS assumed at deployment; `Strict-Transport-Security` header set. | Local dev uses HTTP; documented in README. |
| R | Analyst denies making a decision | Every decision persists `ScoringDecision` with timestamp; every case activity has an `AuthorId` and timestamp. Correlation id on every response for cross-correlation with logs. | Log storage integrity is out of scope. |
| I | Information disclosure of case notes | `risk:investigate` scope required for case list / detail. No unauthenticated endpoints beyond `/health/*` and `/api/v1/auth/token`. | Server-side logs may capture request bodies — logging level should be tuned in prod. |
| D | Denial of service | ASP.NET Core token-bucket rate limiter per authenticated `Name` or client IP. Bounded channels give producer back-pressure. | No CDN / WAF in front of the API. |
| E | Escalating to another scope | `AddAuthorizationBuilder` policies require a matching `scope` claim per endpoint; test covers 403 for wrong scope. | JWT is bearer — leaked tokens are usable until expiry. |

### API ↔ persistence

| Letter | Threat | Mitigation | Residual |
| --- | --- | --- | --- |
| T | Tampering with ruleset JSON | `RulesetSerializer` deserialises via strict `JsonSerializerOptions`; unknown properties are ignored, not silently accepted. Domain constructor invariants catch bad data at load. | No cryptographic signature on ruleset JSON yet — future improvement, listed in README. |
| I | Sensitive data written to logs | EF Core parameterises SQL — no interpolation. Log messages avoid full request bodies. | Ensure `Microsoft.EntityFrameworkCore.Database.Command` log level is `Warning` in prod. |

### Rule authoring ↔ live decisioning

| Letter | Threat | Mitigation | Residual |
| --- | --- | --- | --- |
| T | Rule tampering to whitelist a bad merchant | Only `risk:admin` can `POST /api/v1/rulesets/{v}/activate` or `/shadow`. Every activation is recorded in `Ruleset.ActivatedAt`. | Two-person control on ruleset activation is not enforced yet — natural extension of the case four-eyes pattern. |

### Analyst ↔ case

| Letter | Threat | Mitigation | Residual |
| --- | --- | --- | --- |
| E | Analyst improperly closes a large case as `FalsePositive` | Case state machine + **four-eyes at exposure ≥ 10,000** for `ConfirmedFraud`. Note: `FalsePositive` also warrants four-eyes in some regimes — this is called out in "Residual risk" below. | See residual. |
| R | Analyst denies acting on a case | Every `AddNote`, `Assign`, `ProposeDisposition`, `Approve` records the actor and timestamp on the `Case` aggregate. | Log integrity is out of scope. |

## Mitigations implemented

- **JWT bearer with typed options** (`JwtOptions`) validated at boot; production guard refuses
  boot with the default signing key.
- **Four scoped policies** on every non-anonymous endpoint.
- **DataAnnotations on request DTOs** + domain constructor invariants in `Transaction`, `Money`,
  `GeoLocation`.
- **Rate limiting** via token bucket keyed on user or IP.
- **Security headers** middleware (`nosniff`, `DENY`, `no-referrer`, restrictive
  `Permissions-Policy`, `HSTS`).
- **Correlation id** middleware on every request.
- **Environment guard** — no seeding in `Testing` env, no default-key boot in `Production`.
- **Four-eyes** for large-exposure `ConfirmedFraud` dispositions; approver ≠ proposer enforced in
  domain.
- **PII minimisation** — no PAN, no holder name, no full IP if the deployment marks
  `IpAddress` as PII.

## Residual risk

- **Four-eyes on `FalsePositive` for large exposures** is not currently enforced. A dishonest
  analyst could dismiss a real fraud as false-positive to protect a colluding merchant. Follow-up:
  make four-eyes symmetric across `FalsePositive` and `ConfirmedFraud` at high exposure.
- **Ruleset activation is single-signature**. A dishonest admin could activate a ruleset that
  whitelists a merchant. Follow-up: extend the four-eyes pattern to ruleset activation.
- **JWT key rotation and JWKS** are not implemented — deliberate for a portfolio-scope prototype.
- **Rule evasion (gaming)** — an attacker who knows the rules can craft transactions that skirt
  them (e.g., stay just under thresholds). This is mitigated by shadow-mode challenger evaluation
  and by keeping the exact thresholds internal. In this repo the defaults are documented for
  reviewers, so evasion is trivially possible; a real deployment would keep them out of the client
  bundle.
- **Data-source integrity** — the "IP reputation" list is a static synthetic array. A real
  deployment would consume a feed with signed updates.
- **PII at rest** — SQLite is unencrypted. A real deployment would use TDE or SQLCipher.

## What would change for a real production deployment

- Replace `DevTokenIssuer` with an identity provider (OIDC + JWKS).
- Move JWT signing key to a KMS or HSM; rotate quarterly.
- Postgres with row-level security for tenant isolation.
- Encrypted at rest (TDE); TLS everywhere; mutual TLS between services.
- Central log aggregation (SIEM); immutable audit log for `Case` and `Ruleset` mutations.
- WAF in front of the API; CDN with rate-limit rules.
- Signed rulesets (Ed25519) with a mandatory verification step at load.
- Two-person control on ruleset activation and on `FalsePositive` at high exposure.
- Formal red-team exercise of the rule evasion surface.

## Explicit non-claims

- Not audited by a third party.
- Not certified against PCI-DSS, ISO 27001, SOC 2, or any other framework.
- Not deployed to serve real payment traffic.
- Not tested against real cardholder data — every value in this repo is synthetic.
- Precision / recall numbers in `docs/detection-performance.md` are measured on the seeded
  synthetic dataset and do not predict production performance.
