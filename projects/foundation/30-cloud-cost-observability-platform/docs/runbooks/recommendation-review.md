# Runbook: Recommendation Review

## Trigger
Weekly FinOps review or a high-confidence recommendation enters the backlog.

## Procedure
1. Filter `/api/v1/recommendations` to `Open`; read evidence and exact remediation steps.
2. Confirm owner, change window, workload criticality, commitments, data-retention obligations, and rollback plan.
3. `Accept` only when an accountable owner agrees; capture the current monthly baseline when marking `Implemented`.
4. After a comparable post-change period, call `Verify` with post-change monthly cost. The system computes realised savings as `max(0, baseline - post-change)`.
5. `Dismiss` only before implementation and record the reason outside this compact demo if evidence is insufficient.

## Decision principles
Do not deallocate a resource solely because it is low-utilisation in a synthetic dashboard. Provider tags and utilisation are evidence, not a substitute for owner approval or production change control.
