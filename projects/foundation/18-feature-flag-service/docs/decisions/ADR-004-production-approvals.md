# ADR-004: Require four-eyes approval for production configuration

## Context
A targeting error in production can change user behavior immediately. At the same time, operators must be able to stop a harmful feature during an incident.

## Options
1. Allow all authenticated writers to change production directly.
2. Require approval for every production mutation, including kill switches.
3. Require request/review/approve/apply, with a separately audited emergency kill-switch bypass.

## Decision
Use option 3. The requester cannot review their own request; only an approved request can apply a proposed production snapshot. Kill-switch changes bypass approval and write a distinct audit action.

## Consequences
Routine production changes have explicit separation of duties and a review trail. Emergency mitigation remains fast and discoverable in audits.

## Risks
The workflow slows routine recovery and the current demonstration does not provide time-bound approvals or external ticket validation. A bypass still relies on write-scope protection.

## Alternatives
Change windows and role-only authorization are simpler but do not provide per-change four-eyes evidence. A full external change-management integration is appropriate in an enterprise.
