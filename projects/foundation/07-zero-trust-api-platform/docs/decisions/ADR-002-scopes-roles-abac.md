# ADR-002 — Scopes, roles, and resource-ownership: compose all three

- Status: Accepted
- Date: 2026-08-01

## Context

Every non-trivial API needs a mental model for authorization. The three most common are:

- **Scopes** — coarse capabilities encoded in the token: "this token may do
  `partner.payments.initiate`". Great for machine-to-machine surfaces.
- **Roles** — coarse group memberships: "this user is an `admin`". Great for internal
  operator identities.
- **ABAC / resource attributes** — fine-grained decisions about *this* action on *this*
  resource: "this subject owns this account". Necessary for IDOR safety and multi-tenant
  isolation.

Each on its own is insufficient:

- Scopes alone don't stop Alice from reading Bob's account when both hold the same scope.
- Roles alone don't cleanly represent M2M partner tokens.
- ABAC alone can't cheaply reject a request that lacks the required capability at all.

## Options considered

1. Pick one model (scopes) and force everything else into it.
2. Two separate policy layers (scope check + programmatic ownership check inside the
   endpoint body).
3. Compose all three into ASP.NET Core's `AddAuthorization` model using
   `IAuthorizationRequirement` per axis and stack them in named policies.

## Decision

We chose **option 3**: policies composed from
`ScopeRequirement + RoleRequirement + StepUpRequirement + resource-typed
AccountOwnerRequirement` handlers.

- `customer.read` policy = `ScopeRequirement(customer.read) + aud=customer`.
- `admin.audit` policy = `ScopeRequirement(admin.audit) + StepUpRequirement(mfa,step-up) + aud=admin`.
- Ownership is a separate resource-typed requirement invoked imperatively from within an
  endpoint that has already passed the scope + audience policy:
  `authz.AuthorizeAsync(ctx.User, id, new AccountOwnerRequirement())`.
- A **policy decision point** endpoint (`POST /api/v1/admin/authz/evaluate`) is provided so
  administrators can ask *"would principal X be allowed to do Y on Z, and why?"*, and it
  returns the deciding requirement (the reason). This is a strong differentiator vs
  systems where authorization is a black box.

## Consequences

- Adding a new authorization axis (device posture, session risk score, geo-fence) becomes a
  new `IAuthorizationRequirement` + handler, not a rewrite.
- Policies read declaratively at the composition root.
- The explainable authorization endpoint is directly testable (and tested) — no reliance
  on "why did this deny?" reading of logs.
- Every allow AND deny is written to the audit log with the deciding reason, closing the
  loop between the policy engine and the audit trail.

## Risks

- More types to name and register. Mitigated by a single `Handlers.cs` file.
- Ownership check is imperative (not declarative on the endpoint). Mitigated by a
  consistent pattern across `CustomerEndpoints` and a helper that would centralise it in a
  larger service.

## Alternatives to revisit

- A full policy language (OPA/Rego, Cedar) is the right answer at real enterprise scale.
  The compose-requirements approach here is a stepping stone — the `IAuthorizationHandler`
  registry can be replaced by a policy-engine adapter without changing endpoint code.
