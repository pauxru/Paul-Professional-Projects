# ADR-001 — RS256 + JWKS as default, HS256 only for isolated demos

- Status: Accepted
- Date: 2026-08-01
- Deciders: engineer of record

## Context

The platform issues and validates JWTs for three different audiences (customer, partner,
admin). Two families of signing algorithms are commonly used with JWT bearer auth:

- **HS256** — a shared symmetric secret. Simple to demo, but the API and the issuer must
  share the secret; the same secret used to validate is used to sign; leaking it
  compromises both directions; and it does not compose with a public OIDC provider.
- **RS256** — asymmetric. The issuer keeps a private RSA key; validators fetch the *public*
  key set at `/.well-known/jwks.json`. Every off-the-shelf identity provider (Entra ID,
  Auth0, Okta, Keycloak, AWS Cognito) publishes JWKS.

The realistic production shape of this platform is *Entra ID / Auth0 issues the tokens*
and the API validates via the provider's JWKS. That means the default in this repository
must be RS256 + JWKS — otherwise the "production" configuration path is a big diff from
what the code was actually written against, and the demo would not exercise the same code
paths as production.

## Options considered

1. **HS256 as default, RS256 as an opt-in.** Simplest to demo, wrong shape for production
   parity. Sharing the signing key between issuer and validator is a security anti-pattern
   in any multi-tenant or multi-service deployment.
2. **RS256 as default with an in-repo JWKS endpoint, HS256 supported for isolated demos.**
   The API always fetches its own JWKS; the same validation code runs in dev and prod;
   swapping to Entra ID means changing configuration only.
3. **EdDSA (Ed25519).** Nicer key size, but the .NET JWT libraries' JWKS support for OKP
   keys is less mature than for RSA, and neither Entra ID nor Auth0 emits EdDSA today.

## Decision

We chose **option 2**: RS256 + in-repo JWKS as the default, with HS256 as an alternate
mode for isolated demos or CI environments that cannot host RSA keys.

- The in-repo `JwksProvider` generates and stores RSA keys in the `signing_keys` table.
- `/.well-known/jwks.json` exposes all non-retired active keys.
- The JWT `kid` header is set on issuance; the validator resolves the key from the JWKS.
- Two active keys are allowed simultaneously so a rotation window doesn't force an outage.
- HS256 mode is available via `Jwt:UseRs256=false` and is signposted in code and README as
  a demo shortcut, not a production posture.
- A startup guard refuses to boot in `Production` with the default HS signing key.

## Consequences

- The validation code path in dev matches production; swapping to Entra ID / Auth0 is a
  configuration change only.
- Key rotation is a real, tested capability, not a TODO.
- The `alg=none` attack is rejected explicitly in `TokenValidator` and asserted by a test
  (`Alg_None_Tampered_Token_Rejected`).
- We accept the extra complexity of an RSA key store and rotation runbook.

## Risks

- Key material must be protected in production. This repo's SQLite key store is fine for a
  demo but wholly inappropriate for real deployments; the production runbook says "use
  Azure Key Vault / AWS KMS", not "put your prod key in SQLite".
- JWKS caching in downstream clients can serve stale keys during rotation. Two active keys
  and a documented rotation window mitigate this.

## Alternatives to revisit

- If a future project on the same host cannot use RSA (e.g. embedded), HS256 with a strict
  per-scope key registry could be considered. The abstraction is deliberately open to it.
