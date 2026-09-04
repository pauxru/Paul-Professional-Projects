# Portfolio Summary — Zero-Trust API Platform

## In one line

A .NET 10 modular monolith exposing three distinct API surfaces (customer, partner, admin)
with genuinely different authorization postures — RS256+JWKS with key rotation,
explainable authorization, refresh-token reuse detection, hash-chain audit and a real
API-key → OAuth migration playbook.

## Why this project

Financial-services engagements almost always involve a legacy API-key surface, an OAuth2
partner surface, and an internal admin surface — all with different threat profiles, all
sharing a domain model. Getting the *shape* right is the consulting deliverable. This is
that shape, working, testable, and honestly documented.

## The interesting technical properties

- **Explainable authorization.** `POST /api/v1/admin/authz/evaluate` returns the deciding
  requirement (why a decision was made). It's not a black box.
- **Refresh-token rotation with reuse detection.** Presenting a consumed refresh token
  revokes the whole family. Tested end-to-end.
- **Hash-chain audit.** Every allow AND deny is chained. Tampering is detected in
  `O(n)` verify.
- **API-key migration with cutover.** Dual-accept phase, per-key deprecation, migration
  report, enforced cutover — with runbook and integration tests for each state.
- **Simulated mTLS with honesty.** The `X-Client-Cert-Thumbprint` verification path is
  the same shape a real ingress-terminated mTLS would produce — and ADR-005 documents
  exactly what's real and what's simulated.
- **RS256 + JWKS by default.** Two active keys during rotation; the local issuer and the
  Entra ID configuration run through the same validation path.

## What isn't real

- The identifiers used in demo data (Alice Kimani, Bob Otieno, Acme Treasury Ltd,
  Savanna Logistics Ltd, Northstar Financial Services) are fictional.
- No formal audit, penetration test or compliance certification.
- No production traffic. No revenue. No customer counts. No uptime figures.

## What this looks like on an engagement

You are working with a client whose API-key posture is holding back a compliance
initiative. This project is the demo you show at the whiteboard: "Here is the
dual-accept phase, here is the enforcement toggle, here is what the audit trail looks
like on cutover day, and here is the runbook I would leave with your on-call team."

## Where to start reading

1. [`README.md`](../../README.md) — the tour.
2. [`docs/security/threat-model.md`](../security/threat-model.md) — the interesting document.
3. [`docs/decisions/`](../decisions/) — five ADRs.
4. [`src/ZeroTrust.Api/Program.cs`](../../src/ZeroTrust.Api/Program.cs) — composition root.
5. [`src/ZeroTrust.Infrastructure/Identity/TokenIssuer.cs`](../../src/ZeroTrust.Infrastructure/Identity/TokenIssuer.cs) — the trust core.
