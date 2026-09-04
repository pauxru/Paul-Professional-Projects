# Runbook: Cost Spike Response

## Trigger
An anomaly is high/critical, a budget is approaching/exceeding a threshold, or a team reports unexpected spend.

## Immediate actions
1. Record the correlation ID, reporting period, actual versus amortised basis, and dashboard timestamp.
2. Open `/api/v1/anomalies/groups` and identify the leading contribution. Do not suppress an anomaly before recording the planned-event reason.
3. Query `/api/v1/costs?groupBy=resource` for the affected date/service and `/api/v1/allocations/audit/{costRecordId}` for attribution explanation.
4. Check whether a provider restatement/import changed the day through `/api/v1/imports`.

## Containment
Validate workload ownership with the resource owner/team tag. For suspected runaway compute or AI usage, use the provider's approved operational controls; this local reference implementation does not issue cloud actions. Capture baseline cost before any remediation so savings can be verified.

## Follow-up
Acknowledge the anomaly, create/accept a recommendation where suitable, implement through normal change control, then verify with post-change actual costs. Improve tags/rules if attribution was unclear.
