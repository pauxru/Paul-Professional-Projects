# Security Review — Zero-Trust API Platform

## Scope and method

Every source file in this repository was written for this project. The review method was:

1. Enumerate assets (see `threat-model.md`).
2. Draw trust boundaries and STRIDE each one (see `threat-model.md`).
3. Map every mitigation to a specific file and, where possible, a specific integration
   test that would fail if the mitigation regressed.
4. Record what would not survive a production deployment as-is, honestly.

Sources reviewed:

- `src\ZeroTrust.Api\Program.cs` (composition root)
- `src\ZeroTrust.Api\Authorization\*` (requirements, handlers, PDP, api-key handler)
- `src\ZeroTrust.Api\Middleware\*` (correlation id, security headers, partner posture)
- `src\ZeroTrust.Api\Endpoints\*` (customer / partner / admin / auth)
- `src\ZeroTrust.Infrastructure\Identity\*` (JWKS provider, token issuer, token validator)
- `src\ZeroTrust.Infrastructure\Security\*` (secret hasher, audit log, HMAC webhook)
- `src\ZeroTrust.Domain\**` (invariants)

## Assets

See [`threat-model.md`](threat-model.md) §2.

## Trust boundaries

See [`threat-model.md`](threat-model.md) §1.

## Data classification

| Class | Examples | Handling |
|-------|----------|----------|
| Critical | RSA private signing keys, password hashes | Never returned in any API response |
| High | Refresh tokens, API keys, JWTs | Hashed at rest (refresh, api-key); short-lived; JWTs never persisted |
| Medium | Customer accounts, statements, partner payments | Owner-scoped; audience-scoped; audited |
| Low | Correlation IDs, IPs, user agents | Present in audit records for forensics |

No real personal data. All identifiers are fictional (see the security-standards non-claims).

## Threat model (STRIDE per boundary)

See [`threat-model.md`](threat-model.md) §3 for the twenty-threat table, and §4 for the
two attack trees (partner key compromise, admin account takeover).

## Mitigations implemented

Concrete code + test mapping:

- **Bearer signature validation**: `TokenValidator.cs` sets `RequireSignedTokens=true`,
  `RequireExpirationTime=true`, `ValidateIssuerSigningKey=true`,
  `IssuerSigningKeyResolver` returns the RSA public key from `IJwksProvider`. `alg=none`
  is rejected explicitly.
- **Audience separation**: policies in `Program.cs` include
  `RequireClaim("aud", Audience.Customer|Partner|Admin)`. Tokens with the wrong audience
  return 401 at the JwtBearer layer.
- **Scope enforcement**: `ScopeRequirement` + `ScopeHandler`. Endpoints are wrapped in
  named policies (`customer.read`, `partner.payments.initiate`, `admin.audit`, …).
- **Ownership (IDOR safety)**: `AccountOwnerRequirement` + resource-typed handler over
  `Guid`. `IsOwnedBy(subject)` on the entity.
- **Step-up for admin**: `StepUpRequirement("mfa","urn:ntsf:acr:step-up")` on all admin
  policies. Token issuer stamps `amr` + `acr` claims when the user is `mfaEnrolled` and
  the caller requested admin audience.
- **Refresh rotation + reuse detection**: `TokenIssuer.RefreshAsync` looks up the token
  by hash. If already consumed → revoke the whole family with reason `reuse_detected`.
- **Revocation list**: `RevokedToken` table + validator check; audience-scoped.
- **API-key hashing**: PBKDF2 SHA256 100k iterations, per-key salt (`SecretHasher`).
- **API-key cutover**: `IApiKeyToggle` singleton; handler reads on every request; audit
  event `ApiKeyRejectedPostCutover` on reject and `ApiKeyDeprecatedUsed` when a
  deprecated-but-not-enforced key is used.
- **HMAC webhook signature**: v1 scheme (`{version}.{timestamp}.{nonce}.{body}`),
  constant-time compare (`CryptographicOperations.FixedTimeEquals`), replay cache with
  nonce TTL, signature version header for scheme rotation.
- **Audit hash chain**: SHA-256 over the canonical payload including `previousHash`.
  `AuditLog.VerifyChainAsync` re-derives every hash and returns the first failing row.
- **Security headers**: HSTS, X-Content-Type-Options, X-Frame-Options, Referrer-Policy.
- **CORS**: allow-list; never `AllowAnyOrigin + AllowCredentials`.
- **Request size cap**: Kestrel 1 MiB max body; multipart size limit.
- **Correlation IDs**: middleware sets and echoes; every audit record carries the value.
- **ProblemDetails**: `AddProblemDetails` + `UseExceptionHandler` centralised.
- **Rate limiter**: partner token-bucket (per `partner_code`), admin fixed-window (per
  `sub`); `429` + `Retry-After`; rate-limit hits audited.
- **Partner posture**: IP allow-list + client cert thumbprint (simulated), documented.
- **Break-glass**: two-person rule + ≥10-char justification enforced in the domain
  constructor.

## Residual risk

See [`threat-model.md`](threat-model.md) §5.

## What would change for a real production deployment

- Signing keys stored in Azure Key Vault (or AWS KMS), never in the app database.
- HTTPS everywhere; `RequireHttpsMetadata = true`. HSTS with preload.
- Real ingress-terminated mTLS for the partner surface (see ADR-005); ingress must strip
  caller-set `X-Client-Cert-Thumbprint`.
- OIDC provider (Entra ID / Auth0) as the token issuer; local issuer removed or restricted
  to development.
- Multi-instance-safe rate limiter (Redis-backed bucket store).
- Audit records streamed to a WORM sink (S3 Object Lock / Azure Immutable Blob) with a
  Merkle-tree proof anchor.
- Structured logs shipped to a SIEM.
- Continuous access evaluation for admin sessions.

## Explicit non-claims

This is a self-directed engineering demonstration. **No formal security audit, penetration
test, or compliance certification (PCI DSS, SOC 2, HIPAA, ISO 27001) has been performed or
is claimed.** The identifiers used in demo data (Alice Kimani, Bob Otieno, Acme Treasury
Ltd, Savanna Logistics Ltd, Northstar Financial Services) are fictional. The mTLS check is
simulated at the application layer via a request header; see ADR-005.
