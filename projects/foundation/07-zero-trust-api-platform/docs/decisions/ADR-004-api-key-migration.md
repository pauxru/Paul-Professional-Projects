# ADR-004 — API-key migration strategy: dual-accept phase + enforced cutover

- Status: Accepted
- Date: 2026-08-01

## Context

In most real consulting engagements the client has an existing API with a legacy API-key
authentication mechanism, and the goal is to migrate to OAuth2 client credentials without
outages. The common shape of that migration has three phases:

1. **Dual-accept phase**: both API keys and JWTs are accepted. Partners can begin issuing
   OAuth-signed requests but existing integrations keep working.
2. **Deprecation notice**: individual keys carry a `DeprecatedAfterUtc` timestamp. Requests
   with a deprecated key succeed but are flagged in the audit log.
3. **Enforced cutover**: an operator flips the enforcement switch. Any key past its
   deprecation date is rejected. Any partner still holding a live key beyond that point
   must have already migrated or has scheduled downtime.

The engineering choice is where the enforcement toggle lives, how the deprecation date is
tracked, and what visibility ops has into "who is still using a key".

## Options considered

1. **No transition; hard cutover.** Zero engineering, unacceptable operational risk.
2. **Toggle in config.** Flipping an appsettings value requires an app restart and only
   affects a single instance; unfit for a multi-instance deployment.
3. **Toggle as a singleton service that the auth handler reads on each request; a per-key
   deprecation date on the API-key row; a migration report endpoint enumerating recent
   usage.** Operator can flip enforcement without a restart; can also see who is still
   dependent on the old scheme before flipping.

## Decision

We chose **option 3**.

- `IApiKeyToggle` is a singleton mutated by
  `POST /api/v1/admin/api-keys/cutover { enforcementActive: true|false }`.
- The `ApiKeyAuthenticationHandler` reads `_toggle.EnforcementActive` on every request.
  If enforcement is on and the presented key's `DeprecatedAfterUtc <= now`, the key is
  rejected with reason `api_key_past_cutover` (audited).
- If enforcement is off but the key is past deprecation, the request succeeds *and* an
  audit event `ApiKeyDeprecatedUsed` is written so ops can see who to nudge.
- `GET /api/v1/admin/api-keys/migration-report` shows per-key: last-used timestamp, usage
  count, deprecated-after date, and whether that key is still needed.

## Consequences

- Migrations are visible before they are enforced. Ops can see the graph of "how many
  requests per hour still use API keys" without waiting for support tickets after the
  flip.
- Rollback is instant: flip enforcement back to off.
- Every rejected post-cutover request is on the audit trail, so support conversations
  ("why did my client stop working at 14:03 UTC?") are answerable in one query.
- Per-key deprecation dates let staged migrations happen: partner A moves early, partner
  B moves later.

## Risks

- The toggle is process-local. A multi-instance deployment would need to propagate the
  toggle change to all instances (a config-server or feature-flag service would be the
  production pattern). For this demo, single-process is honest.
- Deprecation date is enforced only when `EnforcementActive` is on. That is by design (it
  is the point of dual-accept). The audit event `ApiKeyDeprecatedUsed` covers the gap.

## Alternatives to revisit

- A feature-flag service (LaunchDarkly, ConfigCat, Azure App Configuration) or a Consul
  KV holding the toggle would fit a multi-instance deployment.
- The migration report could be extended into a "cutover readiness" dashboard with a
  simple SPA — not in scope here.
