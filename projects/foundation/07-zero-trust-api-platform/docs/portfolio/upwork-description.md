# Upwork Portfolio Description

## Title

Secure Zero-Trust API Platform (.NET 10) — self-directed engineering case study

## Body

Problem: Financial-services organisations expose the same core system to end customers,
partner integrators and internal operators — three very different threat profiles. Getting
the authentication and authorization shape right for each surface, and getting a legacy
API-key surface migrated to OAuth2 without downtime, is a recurring senior consulting
deliverable.

Built: A single .NET 10 modular monolith hosting three API surfaces at
`/api/v1/{customer|partner|admin}/*`, each with its own authorization posture. RS256+JWKS
signing with a working key-rotation runbook, refresh-token rotation with reuse detection
that revokes the whole family, explainable policy authorization with a policy decision
point endpoint, HMAC-signed outbound webhooks with a replay cache, an append-only audit
log with a SHA-256 hash chain, and a real API-key migration playbook (dual-accept phase,
per-key deprecation, enforced cutover).

Engineering focus:

- Explainable authorization (`POST /admin/authz/evaluate` returns the deciding requirement).
- Refresh-token rotation with reuse detection.
- Hash-chain audit trail with `O(n)` tamper-detection.
- API-key migration with a runbook and integration tests for every migration state.
- Simulated mTLS with honest ADR documentation of what's real vs. terminated at ingress.
- RS256 + JWKS with a working two-key rotation window.

Stack: .NET 10, ASP.NET Core minimal APIs, EF Core over SQLite, xUnit,
`WebApplicationFactory<Program>`, System.IdentityModel.Tokens.Jwt, ASP.NET Core rate
limiter, OpenTelemetry.

Verification: `dotnet build -c Release` and `dotnet test -c Release` both succeed on a
clean checkout. 65 tests pass (27 unit + 38 integration). Real console output is in
`docs/test-results.md`. Threat model in `docs/security/threat-model.md`. Five ADRs.
Four runbooks (key rotation, revoke a partner, break-glass, api-key cutover).

This is a self-directed portfolio project, not client work. No customers, no revenue,
no compliance certification. Identifiers in the demo data (Alice Kimani, Bob Otieno,
Acme Treasury Ltd, Savanna Logistics Ltd, Northstar Financial Services) are fictional.
