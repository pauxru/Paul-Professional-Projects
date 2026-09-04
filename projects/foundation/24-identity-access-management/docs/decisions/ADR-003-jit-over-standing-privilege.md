# ADR-003 — JIT elevation over standing privilege

## Context

Privileged access presents disproportionate risk. Operators still need temporary access for approved work and incidents.

## Options

1. Permanent privileged role membership.
2. Shared break-glass accounts.
3. Time-boxed elevation represented as a first-class effective-access path.

## Decision

Use JIT elevation with mandatory justification and ticket reference, optional approval, start/end times, session recording metadata, an audit alert on creation, early revocation, and automatic expiry through an `IClock`-driven worker.

## Consequences

- Privilege is absent before activation and after expiry.
- The authorization engine and access report show the ticket-derived path.
- `FakeClock` can prove expiry removes permission without sleeping.
- Every elevation creates high-signal audit evidence.

## Risks

- Worker failure could delay status cleanup, although the resolver independently checks `EndsAt`.
- A compromised approver can still approve malicious temporary access.
- Recording references do not prove that an external recording system captured a session.

## Alternatives

Standing privilege was rejected as inconsistent with least privilege. Shared accounts were rejected because they damage attribution and credential hygiene.
