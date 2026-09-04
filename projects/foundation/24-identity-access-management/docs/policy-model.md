# Policy Model and Evaluation Algorithm

## Purpose

The engine answers:

> Can subject **X** perform permission **Y** on resource **Z** in environment **E**, and why?

It returns an allow/deny decision, human-readable explanation, decisive policy identifier when applicable, every policy trace, and every effective access derivation matching the requested permission.

## Inputs

```json
{
  "userId": "GUID",
  "permission": "app:finance/payment:approve",
  "resource": {
    "owner": "GUID",
    "classification": "Confidential",
    "costCentre": "CC-FIN"
  },
  "environment": {
    "timeOfDay": "derived from IClock",
    "ipAddress": "10.20.30.40",
    "networkZone": "Corporate",
    "deviceTrust": "Trusted",
    "mfaLevel": 2
  }
}
```

The subject comes from the persisted identity and exposes: `id`, `department`, `jobTitle`, `managerId`, `location`, `costCentre`, `employmentType`, `clearance`, and `status`.

## Effective entitlement derivation

Before evaluating policies, the service resolves:

1. active, unrevoked, unexpired direct entitlement grants;
2. active role grants and every transitive role-inheritance path;
3. static and persisted dynamic group memberships, group roles, and every role path;
4. active JIT elevation whose `StartsAt <= now < EndsAt`;
5. user-entitlement exclusions created by certification; excluded entitlements are removed from all paths.

Each remaining path is retained. Duplicate paths are removed, but multiple different paths to one entitlement remain.

Terminated, suspended, or pending identities are denied before policy evaluation.

## Policy representation

```json
{
  "name": "Finance approval requires trusted corporate context",
  "effect": "Allow",
  "permissionPattern": "app:finance/payment:*",
  "priority": 500,
  "conditionsJson": "{\"subject\":{\"department\":\"Finance\",\"clearance\":\">=3\"},\"resource\":{\"costCentre\":\"${subject.costCentre}\"},\"environment\":{\"networkZone\":\"Corporate\",\"deviceTrust\":\"Trusted\",\"mfaLevel\":\">=2\",\"timeOfDay\":\"08:00-18:00\"}}",
  "enabled": true
}
```

`conditionsJson` contains optional `subject`, `resource`, and `environment` objects. Every condition is ANDed. Arrays mean “one of these values”. Policies cannot execute arbitrary code.

## Supported comparisons

| Form | Meaning |
|---|---|
| `"Finance"` | case-insensitive equality |
| `"app:*"` / `"10.20.*"` | `*` wildcard |
| `">=3"`, `"<2"`, `"==4"` | numeric comparison |
| `["Finance", "Risk"]` | any expected array value |
| `"${subject.id}"` | current subject GUID |
| `"${subject.costCentre}"` | current subject cost centre |
| `"08:00-18:00"` | UTC time range; overnight ranges such as `22:00-05:00` supported |

IP address support in this implementation is exact or wildcard text matching, not CIDR arithmetic.

## Permission matching

Patterns are anchored, case-insensitive wildcard matches:

- `app:finance/payment:approve` matches only that permission.
- `app:finance/*` matches all Finance application permissions.
- `app:*/configuration:administer` matches administration permission for any application.

## Specificity

The engine calculates:

```text
specificity = count(non-wildcard characters in permission pattern)
            + 10 * count(conditions)
```

Specificity only breaks ties after priority and within the same effect. It can never make an allow override a matching deny.

## Exact evaluation algorithm

1. Reject immediately if identity status is not `Active`.
2. Resolve effective derivations and filter to the requested permission.
3. Select every enabled policy.
4. For each policy:
   1. wildcard-match `PermissionPattern`;
   2. if it matches, evaluate every condition;
   3. record permission match, condition match, effect, priority, specificity, and individual reasons.
5. Sort traces by descending priority, descending specificity, then name for deterministic presentation.
6. Find all matching denies.
   - If any exist, choose the highest priority, then highest specificity deny.
   - Return **Deny**. This is global deny-wins precedence.
7. Otherwise find all matching allows.
   - If any exist, choose the highest priority, then highest specificity allow.
   - Return **Allow**.
8. Otherwise, if at least one active matching entitlement derivation exists, return **Allow** from derived RBAC/group/JIT access.
9. Otherwise return **Deny** by default.
10. Append an audit record for externally requested decisions and record decision latency.

## Why deny is global

A deny encodes a safety boundary such as “untrusted device” or “MFA below level 2”. Letting an allow priority override it makes policy composition fragile and creates a privilege escalation path. Policy authors must disable or narrow the deny explicitly.

## Explanation response

```json
{
  "allowed": false,
  "decision": "Deny",
  "explanation": "Denied by explicit policy 'Untrusted device deny'. Explicit deny overrides all grants and allows.",
  "decisivePolicyId": "GUID",
  "derivations": [
    {
      "permission": "app:finance/vendor:edit",
      "path": ["dynamic-group:finance", "group-role", "role:operator", "entitlement:edit-vendors"]
    }
  ],
  "policiesEvaluated": [
    {
      "policyName": "Untrusted device deny",
      "effect": "Deny",
      "priority": 1,
      "specificity": 45,
      "permissionMatched": true,
      "conditionsMatched": true,
      "reasons": [
        "Permission matched.",
        "environment.deviceTrust matched (actual='Untrusted', expected='Untrusted')."
      ]
    }
  ]
}
```

## What-if simulation

`POST /api/v1/policies/simulate` does not persist the proposal. It:

1. loads active identities and current policies;
2. evaluates each identity before the change;
3. replaces the same policy ID or appends a new proposed policy;
4. evaluates each identity after the change with the supplied permission/resource/environment;
5. returns gained user IDs, lost user IDs, unchanged allowed count, and unchanged denied count.

Production scale should execute this as a bounded asynchronous job with policy versioning and a result snapshot.

## Examples

### Explicit deny wins over role access

A role grants `app:finance/payment:approve`, but environment `mfaLevel=1` matches a deny `<2`. Result: deny, with the role path still shown as evidence that deny overrode a grant.

### Resource owner comparison

Policy condition `"owner":"${subject.id}"` matches only when the resource's owner string is the subject GUID.

### Default deny

No derivation and no matching allow means deny even if several policies were evaluated but did not match.
