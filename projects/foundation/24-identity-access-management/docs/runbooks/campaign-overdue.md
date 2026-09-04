# Runbook — Certification Campaign Overdue

## Trigger

- campaign deadline elapsed;
- campaign status is `Overdue`;
- `iga.campaign.completion` remains below 100%;
- reviewers report unexpected automatic revocation.

## Expected system behavior

The deadline worker marks every still-pending item `AutoRevoked`, creates a user-entitlement exclusion, marks the campaign `Overdue`, and appends audit evidence. Authorization should immediately stop resolving that entitlement, including access inherited through roles or dynamic groups.

## Assess

1. Retrieve campaigns and progress:

   ```powershell
   $campaigns = Invoke-RestMethod -Headers $headers http://localhost:5024/api/v1/campaigns
   Invoke-RestMethod -Headers $headers `
     http://localhost:5024/api/v1/campaigns/$campaignId/progress
   ```

2. Retrieve items and identify pending/auto-revoked users:

   ```powershell
   Invoke-RestMethod -Headers $headers `
     http://localhost:5024/api/v1/campaigns/$campaignId/items
   ```

3. Confirm scope, deadline, reviewer mode, owner/manager assignments, and communications.

## Manual enforcement

If the worker has not processed an elapsed deadline:

```powershell
Invoke-RestMethod -Method Post -Headers $headers `
  http://localhost:5024/api/v1/campaigns/auto-revoke-overdue
```

Do not extend a deadline by directly updating the database. Create a replacement campaign if governance approves a new review window.

## Verify revocation

For a sample and every privileged item:

1. retrieve the user access profile;
2. evaluate the exact permission;
3. confirm `allowed=false` unless a separate ABAC allow policy independently grants it;
4. update/provision relevant targets and reconcile;
5. inspect audit for `certification.auto-revoked`.

If an ABAC allow still grants the permission, treat it as a policy-design finding; campaign exclusion removes entitlement derivations but does not override an explicit ABAC allow. Add/narrow a deny or redesign policy through the controlled policy process.

## Restore legitimate access

Auto-revoked access must be restored by a new justified access request or JIT elevation. The normal approval and SoD checks apply. Do not delete the exclusion directly.

## Escalation

- Privileged item not revoked: security and IGA service owner immediately.
- Connector still grants target permission: application owner; use failed-provisioning runbook.
- Wrong reviewer assignment at scale: campaign owner and HR data owner.
- Excessive auto-revocation business impact: governance lead; pause downstream provisioning only under an approved incident decision, not by changing historical items.

## Lessons learned

Record reviewer capacity, SLA reminders, delegation coverage, data quality, and scope size. For production, add reminder schedules, reviewer-out-of-office integration, and asynchronous campaign partitions.
