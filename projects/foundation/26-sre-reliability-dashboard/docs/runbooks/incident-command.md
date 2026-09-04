# Runbook: Incident Command

## Declare
Create an incident with severity, affected services, commander, communications lead, incident start, and author. Add a detection event if it differs from declaration. The status page begins in `Investigating`.

## Command loop
1. Commander sets objective, cadence, and decision owner.
2. Communications lead sends concise impact/status updates.
3. Responders add timestamped evidence and actions to the timeline.
4. Record acknowledgement, then a mitigation when customer impact is reduced.
5. Continue monitoring; mitigation is not resolution.

## Resolve
Resolve only from `Mitigated` after verification. Resolution changes the status-page state to `Resolved` and automatically attributes bad events/budget impact across affected service SLOs for the incident window.

## Handoff
State current impact, recent changes, hypotheses, next action, owner, and the latest timeline timestamp. Do not hand off vague ownership.
