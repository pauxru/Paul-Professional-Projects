# Least-Privilege Guide

## Objective

Grant the smallest permission set, for the shortest time, through the most reviewable derivation. This guide applies to the Northstar Group (fictional) demonstration and is intended as an operating model for a real IGA deployment.

## Preferred access order

1. **Birthright read access** for an unambiguous employment population.
2. **Dynamic group → role** when membership is a stable attribute rule.
3. **Requested role** when several entitlements form a coherent job function.
4. **Requested entitlement** for a narrow exception.
5. **JIT elevation** for privileged operations.
6. Never use an unmanaged target-side grant as the intended path.

## Entitlement catalogue rules

- Use one business action per entitlement.
- Name the application/resource/action in the permission: `app:billing/invoice:approve`.
- Write descriptions for reviewers: state the business outcome and risk, not an API implementation.
- Assign an accountable owner.
- Mark administrative, payment, production, security, and identity-management permissions privileged.
- Review critical risk ratings at least quarterly.

## Role design

- Keep roles job-function oriented and application-independent where possible.
- Use inheritance only where “is a subset of” is stable.
- Avoid a hierarchy that reviewers cannot draw.
- Never create a broad “super user” role to simplify provisioning.
- Use the derivation report before adding an entitlement; the role may already inherit it.
- Run cycle detection through the service/API; do not write DAG edges directly.

## Dynamic groups

- Base rules on authoritative, normalized attributes.
- Prefer equality and small AND expressions.
- Treat manager, department, employment type, clearance, location, and cost centre changes as governance events.
- Re-evaluate immediately after attribute updates.
- Do not add people manually to a dynamic group; fix the source attribute or use an explicit request.

## Access requests

- Require a business justification naming task and expected outcome.
- Add a duration when the need is temporary.
- Preserve manager → owner → security staging for high-risk items.
- Delegation is temporary approval routing, not transfer of ownership.
- Reject vague “just in case” requests.
- Investigate preventive SoD blocks rather than splitting one request into several to evade detection.

## Privileged access

- Use JIT instead of standing membership.
- Require a ticket and specific justification.
- Keep duration shorter than the task window.
- Require approval for planned work; reserve optional approval for tightly controlled emergency policy.
- Attach a session recording reference where the target supports recording.
- Revoke early when the task ends.
- Alert on every elevation and review expired/active inventory daily.

## Policy authoring

- Start from default deny.
- Prefer narrow permission patterns and explicit conditions.
- Use denies for hard safety boundaries such as untrusted devices or insufficient MFA.
- Simulate before activation and review gained/lost identities.
- Version and peer-approve production policy changes.
- Avoid a broad ABAC allow that bypasses catalogue ownership.

## Separation of duties

- Model toxic outcomes, not only role names.
- Detect against resolved entitlements so nesting cannot hide a combination.
- Require named approval, justification, compensating control, and short expiry for exceptions.
- Review expiring exceptions before renewal; do not auto-renew.
- Scan the estate after imports, role changes, and reconciliation.

## Certification

- Scope campaigns narrowly enough for informed decisions.
- Assign managers for workforce appropriateness and owners for application risk.
- Show every derivation path.
- Require justification for bulk actions.
- Auto-revoke unanswered items at the deadline only when stakeholders have agreed to fail closed.
- Restore revoked access through a new justified request, not direct database changes.

## Leavers and dormant access

- Terminate the identity and internal effective access before waiting for connectors.
- End sessions and active elevation immediately.
- Disable target accounts, then reconcile for residual enabled/orphan state.
- Review dormant active accounts against employment facts; do not infer termination solely from login absence.

## Metrics to review

- Pending approvals by SLA age.
- Active and soon-expiring JIT elevations.
- Campaign completion and auto-revocation count.
- SoD violations without active exceptions.
- Orphan accounts, rogue grants, and quarantine age.
- Privileged entitlement holders and number of derivation paths.
- Deny decision rate and decision latency.
