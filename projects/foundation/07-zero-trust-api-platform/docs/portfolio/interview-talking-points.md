# Interview Talking Points

Short answers you can give under time pressure. Each maps to a file so you can pivot.

## "Tell me about a hard authorization problem you've solved."

*"Three API surfaces, three authorization postures, one process. I built it so that
adding a new axis of authorization — device posture, session-risk score, geo-fence — is
a new `IAuthorizationRequirement` plus a handler, not a rewrite. The decisions are
inspectable: `POST /admin/authz/evaluate` returns the requirement that decided the
answer. Auditors love this because it turns authorization from a black box into a
provable component. See `Handlers.cs` and `AuthorizationExplainer.cs`."*

## "How would you migrate a legacy API-key API to OAuth2 without breaking clients?"

*"Three phases. Phase 1: dual-accept — both API keys and JWTs valid. Phase 2: per-key
deprecation date, and a migration report endpoint that shows me who's still on the old
scheme and how much they use. Phase 3: an enforcement toggle read on every request. If
enforcement is on and the key is past its deprecation date, reject with a specific
reason. Rollback is instant: flip the toggle back. There's a runbook and integration
tests for every state. See `docs/runbooks/api-key-cutover.md` and
`ApiKeyMigrationTests.cs`."*

## "Refresh tokens. Rotation? Reuse detection? Why?"

*"Rotation on every use means a leaked historical token can't be used forever. Reuse
detection means if a consumed refresh token shows up again — because the attacker used
it after the legitimate user rotated, or vice versa — I treat that as a compromise
signal and revoke the whole token family. The next legitimate refresh in the family
fails, forcing re-auth. It's the OAuth 2.1 recommended pattern; see ADR-003. Test:
`AuthAndJwksTests.Refresh_Reuse_Revokes_Family`."*

## "JWKS. Why not HS256?"

*"HS256 is a symmetric secret shared between issuer and validator. Anything that can
validate can also mint. That's fine for demos, wrong for production. RS256+JWKS is the
shape every real IdP publishes; the validation code path in dev is exactly the code
path in prod once you point `Jwt:Authority` at Entra ID or Auth0. See ADR-001. The
`alg=none` attack is rejected explicitly in `TokenValidator.cs`, with a test."*

## "How do you keep audit trails honest?"

*"Append-only rows, and every row's hash includes the previous row's hash. Tampering
is detected by re-running the chain. `GET /admin/audit/verify-chain` returns the first
failing sequence number. See `AuditLog.cs` and the two hash-chain tests in
`AuditHashChainTests.cs`. In a real deployment I'd also stream the audit records to a
WORM sink so tampering the database still leaves evidence in the sink."*

## "You call it mTLS but it's a header. What's real and what isn't?"

*"Honest question, honest answer: the transport-layer termination is not real in this
demo — I don't have a working ingress. But the check *shape* is real: a per-partner
thumbprint registry, a request-header comparison, a 401 with an audit event on mismatch.
A production deployment moves the termination to the ingress and configures the ingress
to strip caller-set values on the same header name. See ADR-005 — I signposted this
explicitly rather than dressing it up."*

## "What breaks at 3am?"

- **A signing key is compromised.** → `docs/runbooks/key-rotation.md`. Rotate, publish
  new JWKS, retire the old key after the longest access-token lifetime. Two active keys
  during the window mean no forced logout.
- **A partner is compromised.** → `docs/runbooks/revoke-a-partner.md`. Disable
  partner, revoke live tokens, revoke API keys, notify partner out-of-band, verify
  audit chain is clean.
- **A legacy client is depending on a key past cutover.** → `docs/runbooks/api-key-cutover.md`.
  Roll back the toggle, extend the deprecation date, communicate.
- **An admin needs to act and the approver is unreachable.** → `docs/runbooks/break-glass.md`.
  Two-person rule, ≥10-char justification, time-boxed window, one-shot use, every
  event on the audit trail.

## "What would you change first for a real deployment?"

*"Signing keys move to Key Vault / KMS immediately. Second, real ingress-terminated
mTLS for the partner surface. Third, the rate-limit partitions become a Redis-backed
shared bucket so they work across instances. Fourth, the audit stream shipped to a
WORM sink. See the 'What would change for a real production deployment' section of
`docs/security/security-review.md`."*
