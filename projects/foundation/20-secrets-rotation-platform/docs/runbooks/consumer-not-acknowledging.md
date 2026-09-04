# Runbook: Consumer Not Acknowledging

## Trigger

A dual-write rotation remains in `AwaitingAcknowledgement`, or a consumer approaches the
acknowledgement deadline.

## Procedure

1. Query rotation status and identify acknowledgements still in `Pending`.
2. Ask the consumer to query `/api/v1/consumers/{id}/pending`; notices contain references only.
3. Check webhook retries/DLQ, in-app inbox, and the simulated email outbox.
4. Confirm the workload JWT has `secrets.read` and `secrets.ack`.
5. Confirm a path policy grants the workload value read on the exact hierarchy.
6. Confirm it fetched the staged explicit version, replaced its bounded cache, exercised the new
   credential, and then called the acknowledgement endpoint.
7. Never manually mark an acknowledgement without evidence from the consumer owner.

## Deadline behavior

At timeout the engine marks outstanding acknowledgements `TimedOut`, revokes the candidate, and
retains the prior current version. This is the safe default. Start a new rotation only after the
consumer issue is corrected.

## Escalation

If the old credential is compromised, switch to the emergency revocation runbook; availability may
need to be sacrificed for containment. If the consumer is permanently retired, remove its registry
link through a reviewed metadata change before retrying.

## Exit criteria

The consumer either acknowledges after a verified refresh or the rotation rolls back cleanly, and
the ownership/subscription record is corrected.
