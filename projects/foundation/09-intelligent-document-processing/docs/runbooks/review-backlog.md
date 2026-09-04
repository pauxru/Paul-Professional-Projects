# Runbook — Review backlog

The human review queue is growing faster than reviewers can clear it, or tasks are breaching SLA.

## Symptoms

- `GET /api/v1/review/queue` returns a large or growing list.
- The review-queue-depth metric trends upward.
- Tasks show past-due SLA (`slaDueUtc` in the past).
- The STP rate is low, so most documents require review.

## Background — how the queue prioritises

`ReviewService` orders open tasks by a priority that blends **document value, age and (low)
confidence**, so the most valuable and most-aged items surface first. Tasks are **claimed** with a
**lease**; an abandoned claim expires so the task returns to the queue rather than being stuck on one
reviewer. A concurrent claim on an already-claimed task is rejected (`ReviewClaimConflictException`).

## First checks

```powershell
$h = @{ Authorization = "Bearer $token" }   # needs review:process
$q = Invoke-RestMethod "http://localhost:5009/api/v1/review/queue" -Headers $h
$q.items | Select-Object taskId, documentValue, ageMinutes, priority, slaDueUtc | Format-Table
Invoke-RestMethod "http://localhost:5009/api/v1/metrics/stp" -Headers $h |
  Select-Object reviewQueueDepth, inReview, straightThroughRate
```

1. Look at **why** items are in review — open a few and read their failed validations / low-confidence
   fields (`GET /api/v1/documents/{id}`).
2. Identify **clusters**: are most tasks from one supplier or one failure type?

## Likely causes and actions

| Cause | Evidence | Action |
| --- | --- | --- |
| One supplier's template changed | many tasks, same supplier, same field low-confidence | Correct one document; the learned anchor (ADR-004) lifts confidence for that supplier's future documents |
| Threshold too strict | many tasks just under the auto-approve threshold | Review and, if justified, tune `Pipeline:AutoApproveThreshold` — carefully, it trades safety for STP |
| Genuine spike in volume | queue depth rises across all suppliers | Add reviewers; use **bulk approve** for clean, low-risk clusters |
| Abandoned claims | tasks claimed but idle | Claims auto-expire on lease timeout; no action needed, or shorten `Review` lease |
| SLA breaches | `slaDueUtc` in the past | Prioritise past-due, high-value items first (queue already orders this way) |

## Recovery — drain the backlog

1. **Bulk approve** low-risk clusters (documents that only carry warnings, no hard failures).
2. **Correct-and-learn** on template-change clusters: fixing one document teaches the anchor so the
   rest of that supplier's incoming documents auto-extract and may auto-approve.
3. **Re-balance** by claiming from the top of the prioritised queue (value/age/confidence order).

```powershell
# claim the highest-priority task
$top = $q.items[0].taskId
Invoke-RestMethod -Method Post "http://localhost:5009/api/v1/review/$top/claim" `
  -Headers $h -ContentType application/json -Body '{"reviewer":"me"}'
```

## Escalation / prevention

- Track **correction hotspots** — repeated corrections on the same supplier/field are the highest-ROI
  place to improve extraction (or add a hint), directly reducing future queue volume.
- Alert on review-queue-depth crossing a threshold and on SLA-breach counts.
- Revisit routing thresholds periodically against the measured STP and the false-auto-approve rate;
  never raise the auto-approve threshold's leniency without evidence that it stays safe.
