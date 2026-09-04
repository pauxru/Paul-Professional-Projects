# Exception Backlog
Triage and reduce accumulating open reconciliation exceptions.

## Symptoms
- Open exceptions accumulate in `GET /api/v1/exceptions`.
- Aging report buckets show old unresolved exceptions.
- Operations see high or critical severity items remaining open.

## Preconditions / Access
- API is running on port `5005`.
- Base URL is `http://localhost:5005`.
- Operator has a JWT with `recon:resolve` for assign, comment, resolve, and reopen.
- A different approver has a JWT with `recon:approve` for four-eyes approvals.

Auth preamble:

```bash
BASE_URL="http://localhost:5005"
TOKEN=$(curl -s -X POST "$BASE_URL/api/v1/auth/token" \
  -H "Content-Type: application/json" \
  -d '{"subject":"ops-resolver","scopes":["recon:resolve"]}' \
  | jq -r .accessToken)
APPROVER_TOKEN=$(curl -s -X POST "$BASE_URL/api/v1/auth/token" \
  -H "Content-Type: application/json" \
  -d '{"subject":"ops-approver","scopes":["recon:approve"]}' \
  | jq -r .accessToken)
```

```powershell
$BaseUrl = "http://localhost:5005"
$Token = (Invoke-RestMethod -Method Post "$BaseUrl/api/v1/auth/token" `
  -ContentType "application/json" `
  -Body '{"subject":"ops-resolver","scopes":["recon:resolve"]}').accessToken
$ApproverToken = (Invoke-RestMethod -Method Post "$BaseUrl/api/v1/auth/token" `
  -ContentType "application/json" `
  -Body '{"subject":"ops-approver","scopes":["recon:approve"]}').accessToken
$Headers = @{ Authorization = "Bearer $Token" }
$ApproverHeaders = @{ Authorization = "Bearer $ApproverToken" }
```

## Diagnosis
1. List open exceptions, paging as needed.

   ```bash
   curl -s "$BASE_URL/api/v1/exceptions?status=Open&page=1&pageSize=50" \
     -H "Authorization: Bearer $TOKEN"
   ```

   ```powershell
   Invoke-RestMethod -Method Get "$BaseUrl/api/v1/exceptions?status=Open&page=1&pageSize=50" -Headers $Headers
   ```

2. Prioritize by severity and aging. Start with `Critical` and `High`, then older `Medium` and `Low` items.

   ```bash
   curl -s "$BASE_URL/api/v1/exceptions?status=Open&severity=Critical&page=1&pageSize=50" \
     -H "Authorization: Bearer $TOKEN"
   curl -s "$BASE_URL/api/v1/exceptions?status=Open&severity=High&page=1&pageSize=50" \
     -H "Authorization: Bearer $TOKEN"
   ```

   ```powershell
   Invoke-RestMethod -Method Get "$BaseUrl/api/v1/exceptions?status=Open&severity=Critical&page=1&pageSize=50" -Headers $Headers
   Invoke-RestMethod -Method Get "$BaseUrl/api/v1/exceptions?status=Open&severity=High&page=1&pageSize=50" -Headers $Headers
   ```

3. Use type, currency, and assignee filters for focused queues.

   ```bash
   curl -s "$BASE_URL/api/v1/exceptions?status=Open&type=AmountMismatch&currency=KES&page=1&pageSize=50" \
     -H "Authorization: Bearer $TOKEN"
   curl -s "$BASE_URL/api/v1/exceptions?status=Assigned&assignedTo=ops-resolver&page=1&pageSize=50" \
     -H "Authorization: Bearer $TOKEN"
   ```

4. Check aging buckets.

   ```bash
   curl -s "$BASE_URL/api/v1/reports/aging?format=json" \
     -H "Authorization: Bearer $TOKEN"
   ```

   ```powershell
   Invoke-RestMethod -Method Get "$BaseUrl/api/v1/reports/aging?format=json" -Headers $Headers
   ```

5. Inspect an individual exception before changing it.

   ```bash
   EXCEPTION_ID="<exception-id>"
   curl -s "$BASE_URL/api/v1/exceptions/$EXCEPTION_ID" \
     -H "Authorization: Bearer $TOKEN"
   ```

## Resolution
1. Assign the exception to the resolver.

   ```bash
   curl -s -X POST "$BASE_URL/api/v1/exceptions/$EXCEPTION_ID/assign" \
     -H "Authorization: Bearer $TOKEN" \
     -H "Content-Type: application/json" \
     -d '{"assignee":"ops-resolver"}'
   ```

   ```powershell
   Invoke-RestMethod -Method Post "$BaseUrl/api/v1/exceptions/$ExceptionId/assign" `
     -Headers $Headers -ContentType "application/json" -Body '{"assignee":"ops-resolver"}'
   ```

2. Add an operational comment with evidence and next action.

   ```bash
   curl -s -X POST "$BASE_URL/api/v1/exceptions/$EXCEPTION_ID/comment" \
     -H "Authorization: Bearer $TOKEN" \
     -H "Content-Type: application/json" \
     -d '{"text":"Reviewed source records and provider statement; resolving as ManualMatch."}'
   ```

3. Resolve with one approved reason code: `WriteOff`, `ManualMatch`, `RaiseWithProvider`, `Reprocess`, or `Ignore`.

   ```bash
   curl -s -X POST "$BASE_URL/api/v1/exceptions/$EXCEPTION_ID/resolve" \
     -H "Authorization: Bearer $TOKEN" \
     -H "Content-Type: application/json" \
     -d '{"reason":"ManualMatch","note":"Matched manually after statement review."}'
   ```

4. For `WriteOff` where `|AmountMinor| >= 100000` (`1000.00` in a 2-decimal currency), expect status `PendingApproval`. A different user must approve with `recon:approve`; the proposer cannot approve their own write-off.

   ```bash
   curl -s -X POST "$BASE_URL/api/v1/exceptions/$EXCEPTION_ID/approve" \
     -H "Authorization: Bearer $APPROVER_TOKEN"
   ```

5. If a resolved item was closed incorrectly, reopen it.

   ```bash
   curl -s -X POST "$BASE_URL/api/v1/exceptions/$EXCEPTION_ID/reopen" \
     -H "Authorization: Bearer $TOKEN" \
     -H "Content-Type: application/json" \
     -d '{"note":"Reopened after new provider evidence."}'
   ```

## Verification
- `GET /api/v1/exceptions/{id}` shows the expected status: `Assigned`, `Resolved`, `PendingApproval`, or `Reopened`.
- Four-eyes write-offs at or above `100000` minor units show `ApprovalRequired` and then `ApprovedBy` after approval.
- `GET /api/v1/exceptions?status=Open&...` shows the backlog decreasing.
- `GET /api/v1/reports/aging?format=json` shows older buckets draining.

## Rollback / Escalation
- Use `/reopen` if an exception was resolved incorrectly.
- Use `/reject` with an approver token if a pending write-off should not proceed.
- Escalate if:
  - State transitions fail with `InvalidStateTransitionException`.
  - Approval fails because proposer and approver are the same user.
  - Optimistic concurrency conflicts occur repeatedly.
  - Aging or high-severity exceptions continue growing after triage.
