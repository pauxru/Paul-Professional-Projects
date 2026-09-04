# ADR-005 — Simulated mTLS via `X-Client-Cert-Thumbprint`: honesty over illusion

- Status: Accepted
- Date: 2026-08-01

## Context

Real mutual TLS (mTLS) between partner clients and this API would give us a very strong
identity signal — a certificate presented in the TLS handshake, matched against a per-partner
registry. Configuring real mTLS requires:

- Kestrel `HttpsConnectionAdapterOptions.ClientCertificateMode = RequireCertificate`
- A CA to issue partner certificates (or importable partner-authored certs)
- Partners to configure their HTTP clients to present those certificates
- HTTPS everywhere (not `RequireHttpsMetadata=false`)

None of those things fit inside a portfolio-grade demo that must run with `dotnet build`
and `dotnet test` and no external infrastructure. But we do need to demonstrate the shape
of an mTLS-secured partner surface — the check, the failure modes, the audit — because
that is the shape a real deployment must have.

## Options considered

1. **Pretend to do real mTLS.** Dishonest. If anyone read the code and found it wasn't
   actually verifying certificates, the whole security story falls over.
2. **Skip the mTLS story entirely.** Weaker than reality; a real bank-partner API would
   verify client posture at the transport layer.
3. **Simulate mTLS with a request-header `X-Client-Cert-Thumbprint`, verified against a
   per-partner registry, with the mechanism explicitly documented as a simulation.** The
   verification path, the failure mode, the audit event and the runbook all match the
   real thing; only the transport-layer termination is replaced with a header.

## Decision

We chose **option 3**, with explicit honesty.

- The `Partner` entity carries a `ClientCertThumbprint` column.
- `PartnerPostureMiddleware` verifies the header against the partner's registered
  thumbprint on every partner-surface request; a mismatch is a 401 with an audit event.
- The header is called `X-Client-Cert-Thumbprint` — matching the name that a real gateway
  (nginx, AWS ALB, Azure Application Gateway, Envoy) would populate after terminating
  mTLS at the ingress.
- The README, the ADR, the threat model and the runbook all state that this is a simulation
  and describe what would change for a real deployment.

## Consequences

- The endpoint code that reacts to a missing/incorrect thumbprint is *the same* code that
  would run in a real ingress-terminated mTLS deployment. A production deployment would
  add: (a) ingress-side mTLS with a client-cert issuer, and (b) an ingress rule that
  populates the thumbprint header from `$ssl_client_fingerprint` (or equivalent).
- The tests directly exercise the missing-header and wrong-header failure modes.
- No smoke-and-mirrors: a reader who searches for `X-Client-Cert-Thumbprint` finds an ADR
  and a runbook that says exactly what is real and what is simulated.

## Risks

- A reader who skims the code without reading the ADR might infer that real mTLS is in
  place. Mitigation: the middleware itself has a comment naming the simulation, and the
  security review's non-claims section calls it out.
- Header trust: in production, this header must be *set by the ingress and rejected if
  sent by the caller*, otherwise a caller could spoof it. This project's Program.cs
  documents that constraint; a real deployment must enforce it at the ingress config.

## Alternatives to revisit

- Real Kestrel mTLS with a self-signed CA for the demo — feasible but adds a
  cert-provisioning step to `scripts/demo.ps1` that would make first-run friction higher.
  If this project's audience shifted to "how do you configure Kestrel for mTLS", that
  becomes the right choice.
