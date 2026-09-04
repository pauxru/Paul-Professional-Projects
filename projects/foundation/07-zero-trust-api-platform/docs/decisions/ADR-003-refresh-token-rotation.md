# ADR-003 — Refresh-token rotation with reuse detection

- Status: Accepted
- Date: 2026-08-01

## Context

Refresh tokens are longer-lived than access tokens and represent significant risk if
stolen. Naïve implementations that keep a single refresh token alive for its whole lifetime
have two failure modes:

1. If the refresh token leaks, the attacker can silently mint access tokens for the
   duration of the refresh's validity.
2. There is no signal that reveals the compromise: both attacker and legitimate user
   refresh happily side-by-side.

Rotation on each use (rotate: on every `refresh_token` grant, issue a new refresh and
invalidate the old) plus **reuse detection** (if a refresh token that has already been
consumed is presented again, treat this as a signal of compromise and revoke the *entire
token family*) closes both.

## Options considered

1. **No rotation.** Simplest, worst security posture.
2. **Rotate but do not detect reuse.** Rotation prevents a leaked historical token from
   being used forever, but a stolen currently-valid refresh remains exploitable until
   whichever party refreshes first, and the loser is disconnected without knowing why.
3. **Rotate + detect reuse + revoke family.** The industry standard (OAuth 2.1 draft
   spec; recommended by every major identity provider). If the attacker refreshes first,
   the legitimate user's next refresh is a reuse; both are revoked.

## Decision

We chose **option 3**: rotate + detect reuse + revoke family.

- Every refresh token has a `FamilyId` (a Guid) shared with its ancestors and descendants.
- On refresh, we look up the presented token by its hash.
- If it is *consumed* (already used once), we revoke every token in its family with reason
  `reuse_detected`.
- If it is not consumed, we mark it consumed and mint a new refresh + new access.
- If it is expired or revoked, we return a 401 with a specific reason.

## Consequences

- Compromise of a refresh token is *detectable*: the next legitimate refresh triggers
  family-wide revocation, alerting the user and forcing re-authentication.
- The attack window for a stolen refresh is bounded to whichever party refreshes first.
- Every refresh reuse is written to the audit log.

## Risks

- Legitimate multi-device use: two devices with the same refresh token would look like
  reuse. In this repository each token has a single owning session; multi-device apps must
  each obtain their own refresh (this is the normal OIDC pattern).
- Clock skew: not a factor here because rotation compares consumed-state, not timestamps.
  Expiry uses `IClock`.

## Alternatives to revisit

- Refresh binding to a client device / DPoP (RFC 9449) is the strictly stronger version of
  this posture and is called out as a future improvement.
