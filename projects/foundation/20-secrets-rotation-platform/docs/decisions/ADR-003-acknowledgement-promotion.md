# ADR-003: Gate dual-write promotion on consumer acknowledgements

Status: Accepted

## Context

Sending a notification does not prove a consumer loaded the candidate. Promoting and retiring
without evidence can break slowly deployed, cached, or temporarily offline applications.

## Options

1. Promote immediately after notification.
2. Wait a fixed delay.
3. Track an acknowledgement per linked consumer with a deadline.

## Decision

Use option 3 for dual-write. A rotation creates persisted acknowledgement rows, exposes them through
status and pull endpoints, and advances only when every required consumer acknowledges. The
deadline converts outstanding records to `TimedOut` and automatically rolls back the candidate.

## Consequences

Promotion has explicit evidence and dashboards can identify blockers. Consumer onboarding must
include acknowledgement behavior. Operators can investigate a named non-acknowledging consumer.

## Risks

A compromised consumer can acknowledge without actually refreshing. A permanently offline
consumer can repeatedly block rotation. Production identity must bind the JWT workload to the
consumer ID.

## Alternatives

Immediate promotion was rejected for outage risk. Fixed delay was rejected because time is not
evidence. Quorum acknowledgement was deferred because every declared consumer is initially treated
as required.
